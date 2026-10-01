// Modified by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Foundation;
using SkiaSharp;
using Uno.UI.Runtime.Skia.Native;
using Uno.Foundation.Logging;
using System.Text.RegularExpressions;
using System.Threading;
using Windows.Graphics.Display;
using Windows.Graphics.Interop.Direct2D;
using Microsoft.UI.Xaml.Media;
using Uno.Disposables;
using Uno.UI.Helpers;
using Uno.UI.Hosting;
using Uno.WinUI.Runtime.Skia.Linux.FrameBuffer.UI;
using System.Runtime.CompilerServices;

namespace Uno.UI.Runtime.Skia
{
	internal partial class DRMRenderer : FrameBufferRenderer
	{
		private const uint DefaultFramebuffer = 0;

		private readonly GRContext _grContext;
		private readonly IntPtr _eglDisplay;
		private readonly IntPtr _glContext;
		private readonly IntPtr _eglSurface;
		private readonly int _samples;
		private readonly int _stencil;

		private GRBackendRenderTarget? _renderTarget;
		private SKSurface? _glFbSurface;
		private readonly IntPtr _gbmTargetSurface;
		private readonly int _card;
		private IntPtr _currentBo;
		// Buffer of the page flip in flight; _currentBo stays on screen until it completes
		private IntPtr _pendingBo;
		private readonly uint _crtc;
		private readonly uint _encoder;
		// Frames are rendered and presented on a dedicated thread: an invalidation renders the current
		// state and presents it right away. Invalidations during a pending page flip coalesce into one frame.
		private readonly AutoResetEvent _renderRequested = new(false);
		private readonly ManualResetEventSlim _pageFlipDone = new(true);
		// Display power (DPMS) switched through DRMDisplayPower; no frames are rendered while off
		private readonly object _powerLock = new();
		private readonly object _dpmsLock = new();
		private uint _connectorId;
		private uint _dpmsPropertyId;
		private bool _displayOff;
		private readonly GCHandle _selfHandle;
		private readonly DRMBusyIndicator? _busyIndicator;

		private LibDrm.drmModeCrtc _savedCrtc;
		private uint _savedConnectorId;
		private volatile bool _disposed;
		private bool _crtcRestored;

		public readonly record struct DRMInitOptions(string? CardPath, FramebufferHostBuilder.DRMConnectorChooserDelegate? DRMConnectorChooser, FramebufferHostBuilder.DRMFourCCColorFormat GBMSurfaceColorFormat);

		public unsafe DRMRenderer(IXamlRootHost host, DRMInitOptions drmInitOptions, MouseIndicatorOptions mouseIndicatorOptions) : base(host, mouseIndicatorOptions)
		{
			_selfHandle = GCHandle.Alloc(this);

			if (drmInitOptions.CardPath is not null)
			{
				_card = Libc.open(drmInitOptions.CardPath, Libc.O_RDWR, 0);
				if (_card == -1)
				{
					var errno = Marshal.GetLastWin32Error();
					var errnoStringPtr = Libc.strerror(errno);
					var errorString = Marshal.PtrToStringAnsi(errnoStringPtr);
					throw new InvalidOperationException($"Couldn't open {drmInitOptions.CardPath} ({errno}): {errorString}");
				}
				else
				{
					this.LogInfo()?.Info($"Found DRM device {drmInitOptions.CardPath}");
				}
			}
			else
			{
				var files = Directory.GetFiles("/dev/dri/");

				foreach (var file in files)
				{
					if (DRMCardPathRegex().Match(file).Success)
					{
						_card = Libc.open(file, Libc.O_RDWR, 0);
						if (_card == -1)
						{
							var errno = Marshal.GetLastWin32Error();
							var errnoStringPtr = Libc.strerror(errno);
							var errorString = Marshal.PtrToStringAnsi(errnoStringPtr);
							this.LogDebug()?.LogDebug($"Couldn't open {file} ({errno}): {errorString}");
						}
						else
						{
							this.LogInfo()?.Info($"Found DRM device {file}");
							break;
						}
					}
				}
				if (_card == -1)
				{
					throw new FileNotFoundException("Couldn't open any DRM card matching /dev/dri/card[0-9]+");
				}
			}

			var resources = new DrmResources(_card);
			this.LogDebug()?.Debug($"DRM resources dump:\n{resources.Dump()}");

			if (resources.Connectors.Count == 0)
			{
				throw new Exception("No DRM connectors found");
			}

			var connectors =
				resources.Connectors
				.Where(c => c is { Connection: DrmModeConnection.DRM_MODE_CONNECTED, Modes.Count: > 0 })
				.ToList();
			DrmConnector? connector = default;
			if (drmInitOptions.DRMConnectorChooser is { } chooser)
			{
				var connectorsForChooser =
					connectors
						.Select(c => new FramebufferHostBuilder.DRMConnector((uint)c.ConnectorType, c.ConnectorTypeId, c.Id, c.Name))
						.ToList();
				if (chooser(connectorsForChooser) is var chosenConnectorIndex && connectorsForChooser.Count > chosenConnectorIndex && chosenConnectorIndex >= 0)
				{
					connector = connectors[chosenConnectorIndex];
				}
				else
				{
					throw new InvalidOperationException($"The connector chosen with {nameof(FramebufferHostBuilder.DRMConnectorChooser)} does not have a usable CRTC+encoder combination");
				}
			}
			else
			{
				// We use the first connector that has a usable encoder+crtc combination
				foreach (var connectorCandidate in connectors)
				{
					var encoderIds = resources.Encoders.Keys.AsEnumerable();
					if (resources.Encoders.ContainsKey(connectorCandidate.EncoderId))
					{
						// if connector is already modeset to use a specific encoder, then let's try reusing it first
						encoderIds = encoderIds.Prepend(connectorCandidate.EncoderId);
					}
					foreach (var encoderId in encoderIds)
					{
						var encoder = resources.Encoders[encoderId];
						if (encoder.PossibleCrtcs.Any(crtc => crtc.crtc_id == encoder.Encoder.crtc_id))
						{
							connector = connectorCandidate;
							_encoder = encoderId;
							_crtc = encoder.Encoder.crtc_id;
							break;
						}
						else if (encoder.PossibleCrtcs.Count > 0)
						{
							connector = connectorCandidate;
							_encoder = encoderId;
							// possible crtcs are ordered from best to worst
							_crtc = encoder.PossibleCrtcs.First().crtc_id;
							break;
						}
					}
				}

				if (connector is null)
				{
					throw new InvalidOperationException("Cannot find any connectors with a usable CRTC+encoder combination");
				}
			}

			Debug.Assert(connector is not null && resources.Encoders[_encoder].PossibleCrtcs.Any(crtc => _crtc == crtc.crtc_id));

			var modeInfo = connector.Modes.FirstOrDefault(m => m.IsPreferred, connector.Modes[0]);

			var device = LibDrm.gbm_create_device(_card);
			if (device == IntPtr.Zero)
			{
				throw new InvalidOperationException($"{nameof(LibDrm.gbm_create_device)} failed");
			}
			_gbmTargetSurface = LibDrm.gbm_surface_create(device, modeInfo.Resolution.Width, modeInfo.Resolution.Height, drmInitOptions.GBMSurfaceColorFormat.ToInt(), LibDrm.GbmBoFlags.GBM_BO_USE_SCANOUT | LibDrm.GbmBoFlags.GBM_BO_USE_RENDERING);
			if (_gbmTargetSurface == IntPtr.Zero)
			{
				throw new InvalidOperationException($"{nameof(LibDrm.gbm_surface_create)} failed");
			}

			try
			{
				_eglDisplay = EglHelper.EglGetPlatformDisplay(/* EGL_PLATFORM_GBM_KHR */ 0x31D7, device, null);
				if (_eglDisplay == IntPtr.Zero)
				{
					throw new InvalidOperationException($"{nameof(EglHelper.EglGetPlatformDisplay)} failed : {Enum.GetName(EglHelper.EglGetError())}");
				}
			}
			catch (Exception e)
			{
				this.LogDebug()?.Debug(e.Message);
				_eglDisplay = EglHelper.EglGetPlatformDisplayEXT(/* EGL_PLATFORM_GBM_KHR */ 0x31D7, device, null);
				if (_eglDisplay == IntPtr.Zero)
				{
					throw new InvalidOperationException($"{nameof(EglHelper.EglGetPlatformDisplayEXT)} failed : {Enum.GetName(EglHelper.EglGetError())}");
				}
			}

			(_eglSurface, _glContext, var major, var minor, _samples, _stencil)
				= EglHelper.InitializeGles2Context(_eglDisplay, _gbmTargetSurface);
			if (this.Log().IsEnabled(LogLevel.Information))
			{
				this.Log().Info($"Found EGL version {major}.{minor}.");
			}

			using var _ = MakeCurrent();

			this.Log().Info($"Using {EglHelper.GetGlVersionString()} for rendering.");

			if (!EglHelper.EglSwapBuffers(_eglDisplay, _eglSurface))
			{
				if (this.Log().IsEnabled(LogLevel.Error))
				{
					this.Log().Error($"{nameof(EglHelper.EglSwapBuffers)} failed during Renderer init: {Enum.GetName(EglHelper.EglGetError())}");
				}
			}

			var bo = LibDrm.gbm_surface_lock_front_buffer(_gbmTargetSurface);
			if (bo == IntPtr.Zero)
			{
				throw new InvalidOperationException($"{nameof(LibDrm.gbm_surface_lock_front_buffer)} failed during DRM CRTC setup.");
			}
			var fbId = CreateFbForBo(bo);
			var connectorId = connector.Id;
			var mode = modeInfo.Mode;

			// Save the current CRTC state so we can restore it on exit, which allows
			// the kernel fbcon to reattach and the CLI prompt to reappear.
			var savedCrtc = LibDrm.drmModeGetCrtc(_card, _crtc);
			if (savedCrtc != null)
			{
				_savedCrtc = *savedCrtc;
				_savedConnectorId = connectorId;
				LibDrm.drmModeFreeCrtc(savedCrtc);
			}

			var res = LibDrm.drmModeSetCrtc(_card, _crtc, fbId, 0, 0, &connectorId, 1, &mode);
			if (res != 0)
			{
				throw new InvalidOperationException($"{nameof(LibDrm.drmModeSetCrtc)} failed with error code {res}");
			}

			_currentBo = bo;
			_connectorId = connectorId;
			_dpmsPropertyId = FindConnectorProperty(connectorId, "DPMS");
			DRMDisplayPower.Renderer = this;
			_busyIndicator = DRMBusyIndicator.TryStart(_card, _crtc, device, modeInfo.Resolution.Width, modeInfo.Resolution.Height, () =>
			{
				lock (_powerLock)
				{
					return !_displayOff && !_disposed;
				}
			});

			var glInterface = GRGlInterface.CreateGles(EglHelper.EglGetProcAddress);

			if (glInterface == null)
			{
				throw new NotSupportedException($"{nameof(GRGlInterface)}.{nameof(GRGlInterface.CreateGles)} failed");
			}

			var context = GRContext.CreateGl(glInterface);
			if (context == null)
			{
				throw new NotSupportedException($"{nameof(GRContext)}.{nameof(GRContext.CreateGl)} failed");
			}
			_grContext = context;
			FrameBufferGpu.Attach(this);

			FrameBufferWindowWrapper.Instance.SetSize(new Size(modeInfo.Resolution.Width, modeInfo.Resolution.Height));

			new Thread(PageFlipLoop) { IsBackground = true, Name = "DRM pageflip loop" }.Start();
			new Thread(RenderLoop) { IsBackground = true, Name = "DRM render loop" }.Start();
		}

		private unsafe int CalculateRefreshRate(LibDrm.drmModeModeInfo* mode)
		{
			var res = (int)(mode->clock * 1000000L / mode->htotal + mode->vtotal / 2) / mode->vtotal;

			if ((mode->flags & /* DRM_MODE_FLAG_INTERLACE */ (1 << 4)) != 0)
			{
				res *= 2;
			}

			if ((mode->flags & /* DRM_MODE_FLAG_DBLSCAN */ (1 << 5)) != 0)
			{
				res /= 2;
			}

			if (mode->vscan > 1)
			{
				res /= mode->vscan;
			}

			return res / 1000;
		}

		public override void InvalidateRender()
		{
			if (!_disposed)
			{
				_renderRequested.Set();
			}
		}

		private void RenderLoop()
		{
			// Only this thread renders, so the context stays current here. Binding and releasing
			// it around every frame (MakeCurrent) made the driver flush and revalidate its state each time.
			if (!EglHelper.EglMakeCurrent(_eglDisplay, _eglSurface, _eglSurface, _glContext))
			{
				if (this.Log().IsEnabled(LogLevel.Error))
				{
					this.Log().Error($"{nameof(EglHelper.EglMakeCurrent)} failed on the render thread.");
				}
			}
			while (true)
			{
				_renderRequested.WaitOne();
				if (_disposed)
				{
					return;
				}
				// App work on the render thread (FrameBufferGpu.TryInvoke), also while the display is off
				FrameBufferGpu.RunPending(_grContext);
				// The next frame is rendered while the previous page flip is still pending (one
				// buffer on screen, one queued, one rendered), so CPU/GPU work overlaps the wait for vblank.
				// Without a free GBM buffer it waits for the flip first, as before.
				if (LibDrm.gbm_surface_has_free_buffers(_gbmTargetSurface) == 0)
				{
					_pageFlipDone.Wait();
				}
				lock (_powerLock)
				{
					if (_displayOff)
					{
						// Rendered when the display is switched on again
						continue;
					}
					// The frame rendered below includes every invalidation received so far
					_renderRequested.Reset();
					try
					{
						var started = Stopwatch.GetTimestamp();
						if (!Render())
						{
							// The frame on screen is still current
							continue;
						}
						var bo = SwapBuffers();
						Interlocked.Add(ref DRMDisplayPower.RenderTicksTotal, Stopwatch.GetTimestamp() - started);
						_pageFlipDone.Wait();
						if (_disposed)
						{
							return;
						}
						PageFlip(bo);
						Interlocked.Increment(ref DRMDisplayPower.PresentedFramesCount);
					}
					catch (Exception e)
					{
						// A flip still in flight signals completion itself
						if (_pendingBo == IntPtr.Zero)
						{
							_pageFlipDone.Set();
						}
						if (this.Log().IsEnabled(LogLevel.Error))
						{
							this.Log().Error("Rendering or presenting a DRM frame failed.", e);
						}
					}
				}
			}
		}

		/// <summary>Switches the display off (DPMS) or on; rendering pauses while off, a new frame is presented on wake.</summary>
		internal bool SetDisplayOn(bool on)
		{
			lock (_dpmsLock)
			{
				if (_disposed || _dpmsPropertyId == 0)
				{
					this.LogError()?.Error($"Display power not available (disposed: {_disposed}, DPMS property: {_dpmsPropertyId}).");
					return false;
				}

				if (!on)
				{
					lock (_powerLock)
					{
						_displayOff = true;
					}
					// A page flip on an inactive CRTC fails; let the pending one complete first
					if (!_pageFlipDone.Wait(TimeSpan.FromSeconds(1)))
					{
						this.LogWarn()?.Warn("Page flip still pending while switching the display off.");
					}
				}

				var res = DRMDisplayPowerNative.drmModeConnectorSetProperty(_card, _connectorId, _dpmsPropertyId, on ? DRMDisplayPowerNative.DpmsOn : DRMDisplayPowerNative.DpmsOff);
				if (res != 0)
				{
					this.LogError()?.Error($"Setting DPMS {(on ? "on" : "off")} failed ({res}).");
				}
				else
				{
					this.LogInfo()?.Info($"Display switched {(on ? "on" : "off")}.");
				}

				if (on || res != 0)
				{
					lock (_powerLock)
					{
						_displayOff = false;
					}
					// Present the current state; frames skipped while off are not on screen
					_renderRequested.Set();
				}
				return res == 0;
			}
		}

		private unsafe uint FindConnectorProperty(uint connectorId, string name)
		{
			var connector = LibDrm.drmModeGetConnectorCurrent(_card, connectorId);
			if (connector == null)
			{
				return 0;
			}
			try
			{
				for (var i = 0; i < connector->count_props; i++)
				{
					if (DRMDisplayPowerNative.GetPropertyName(_card, connector->props[i]) == name)
					{
						return connector->props[i];
					}
				}
				return 0;
			}
			finally
			{
				LibDrm.drmModeFreeConnector(connector);
			}
		}

		private IntPtr SwapBuffers()
		{
			using (MakeCurrent())
			{
				if (!EglHelper.EglSwapBuffers(_eglDisplay, _eglSurface))
				{
					if (this.Log().IsEnabled(LogLevel.Error))
					{
						this.Log().Error($"{nameof(EglHelper.EglSwapBuffers)} failed.");
					}
				}
			}
			var nextBo = LibDrm.gbm_surface_lock_front_buffer(_gbmTargetSurface);
			if (nextBo == IntPtr.Zero)
			{
				throw new InvalidOperationException($"{nameof(LibDrm.gbm_surface_lock_front_buffer)} failed");
			}
			return nextBo;
		}

		/// <summary>Queues <paramref name="bo"/> for the next vblank; the previous flip must have completed.</summary>
		private unsafe void PageFlip(IntPtr bo)
		{
			// The pending buffer is on screen now: the one it replaced can be rendered into again
			if (_pendingBo != IntPtr.Zero)
			{
				LibDrm.gbm_surface_release_buffer(_gbmTargetSurface, _currentBo);
				_currentBo = _pendingBo;
				_pendingBo = IntPtr.Zero;
			}

			var fb = CreateFbForBo(bo);
			_pageFlipDone.Reset();
			var res = LibDrm.drmModePageFlip(_card, _crtc, fb, LibDrm.DrmModePageFlip.Event, (void*)GCHandle.ToIntPtr(_selfHandle));
			if (res != 0)
			{
				LibDrm.gbm_surface_release_buffer(_gbmTargetSurface, bo);
				throw new InvalidOperationException($"{nameof(LibDrm.drmModePageFlip)} failed ({res})");
			}
			_pendingBo = bo;
		}

		protected override IDisposable MakeCurrent()
		{
			var glContext = EglHelper.EglGetCurrentContext();
			var readSurface = EglHelper.EglGetCurrentSurface(EglHelper.EGL_READ);
			var drawSurface = EglHelper.EglGetCurrentSurface(EglHelper.EGL_DRAW);
			// Already current (the render thread): nothing to bind or restore
			if (glContext == _glContext && drawSurface == _eglSurface && readSurface == _eglSurface)
			{
				return Disposable.Empty;
			}
			if (!EglHelper.EglMakeCurrent(_eglDisplay, _eglSurface, _eglSurface, _glContext))
			{
				if (this.Log().IsEnabled(LogLevel.Error))
				{
					this.Log().Error($"{nameof(EglHelper.EglMakeCurrent)} failed.");
				}
			}
			return Disposable.Create(() =>
			{
				if (!EglHelper.EglMakeCurrent(_eglDisplay, drawSurface, readSurface, glContext))
				{
					if (this.Log().IsEnabled(LogLevel.Error))
					{
						this.Log().Error($"{nameof(EglHelper.EglMakeCurrent)} failed.");
					}
				}
			});
		}

		private unsafe void PageFlipLoop()
		{
			var ctx = new LibDrm.DrmEventContext
			{
				version = 4,
				page_flip_handler2 = &OnPageFlip
			};
			while (true)
			{
				var pfd = new pollfd { events = 1, fd = _card };
				var res = Libc.poll(&pfd, new IntPtr(1), -1);
				if (res < 0)
				{
					var errno = Marshal.GetLastWin32Error();
					var errnoStringPtr = Libc.strerror(errno);
					var errorString = Marshal.PtrToStringAnsi(errnoStringPtr);
					throw new InvalidOperationException($"{nameof(Libc.poll)} failed ({errno}) : {errorString}");
				}

				res = LibDrm.drmHandleEvent(_card, &ctx);
				if (res != 0)
				{
					throw new InvalidOperationException($"{nameof(LibDrm.drmHandleEvent)} failed ({res})");
				}
			}
		}

		[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
		private static unsafe void OnPageFlip(int fd, uint sequence, uint tv_sec, uint tv_usec, uint crtd_id, void* user_data)
		{
			var handle = GCHandle.FromIntPtr((IntPtr)user_data);
			var @this = (DRMRenderer)handle.Target!;
			if (@this._disposed)
			{
				return;
			}
			@this._pageFlipDone.Set();
		}

		protected override SKSurface UpdateSize(int width, int height)
		{
			_glFbSurface?.Dispose();
			_renderTarget?.Dispose();

			var grSurfaceOrigin = GRSurfaceOrigin.BottomLeft; // to match OpenGL's origin
			var glInfo = new GRGlFramebufferInfo(DefaultFramebuffer, SKColorType.Rgb888x.ToGlSizedFormat());
			_renderTarget = new GRBackendRenderTarget(width, height, _samples, _stencil, glInfo);
			_glFbSurface = SKSurface.Create(_grContext, _renderTarget, grSurfaceOrigin, SKColorType.Rgb888x);

			if (PartialRedraw)
			{
				// Retained frame, copied to the scanout buffer on present (the GBM buffers rotate)
				return SKSurface.Create(_grContext, budgeted: true, new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
					?? throw new InvalidOperationException("Failed to create the DRM retained composition surface.");
			}

			// A full redraw is drawn straight into the scanout buffer instead of a retained
			// surface that was copied to it (a full-screen blit per frame)
			return _glFbSurface ?? throw new InvalidOperationException("Failed to create the DRM framebuffer surface.");
		}

		// Partial redraw into a retained surface. Redrawing the full 1200x1920 frame cost about
		// 17 ms of GPU time per frame on an Atom Z8350, so a small change (a camera image, a value) kept the GPU busy;
		// the opaque copy to the scanout buffer costs a fraction of it. UNO_FRAMEBUFFER_FULL_REDRAW=1 disables it.
		private static readonly bool s_fullRedraw = Environment.GetEnvironmentVariable("UNO_FRAMEBUFFER_FULL_REDRAW") == "1";
		private static readonly SKPaint s_copyPaint = new() { BlendMode = SKBlendMode.Src };

		// Partial redraw needs to know which frame was drawn (see FrameBufferRenderer.Render)
		protected override bool PartialRedraw => !s_fullRedraw && CompositionTargetFrameSlot.IsAvailable;

		// Frames that change most of the screen skip the retained copy
		protected override SKSurface? DirectSurface => PartialRedraw ? _glFbSurface : null;

		protected override void PresentDirect(int degrees, int transX, int transY)
		{
			if (_glFbSurface is { } glFb)
			{
				DrawCursor(glFb.Canvas, degrees, transX, transY);
				glFb.Canvas.Flush();
			}
		}

		protected override void PresentToOutput(int degrees, int transX, int transY)
		{
			if (_surface is { } composition && _glFbSurface is { } glFb)
			{
				if (!ReferenceEquals(composition, glFb))
				{
					composition.Draw(glFb.Canvas, 0, 0, s_copyPaint);
				}
				DrawCursor(glFb.Canvas, degrees, transX, transY);
				glFb.Canvas.Flush();
			}
		}

		private uint CreateFbForBo(IntPtr bo)
		{
			if (bo == IntPtr.Zero)
				throw new ArgumentException("bo is 0");
			var data = LibDrm.gbm_bo_get_user_data(bo);
			if (data != IntPtr.Zero)
				return (uint)data.ToInt32();

			var w = LibDrm.gbm_bo_get_width(bo);
			var h = LibDrm.gbm_bo_get_height(bo);
			var stride = LibDrm.gbm_bo_get_stride(bo);
			var handle = LibDrm.gbm_bo_get_handle(bo).u32;
			var format = LibDrm.gbm_bo_get_format(bo);

			// prepare for the new ioctl call
			var handles = new uint[] { handle, 0, 0, 0 };
			var pitches = new uint[] { stride, 0, 0, 0 };
			var offsets = new uint[4];

			var ret = LibDrm.drmModeAddFB2(_card, w, h, format, handles, pitches,
				offsets, out var fbHandle, 0);
			if (ret != 0)
			{
				throw new InvalidOperationException($"{nameof(LibDrm.drmModeAddFB2)} failed {ret}");
			}

			LibDrm.gbm_bo_set_user_data(bo, new IntPtr((int)fbHandle), OnBoFree);

			return fbHandle;
		}

		private void OnBoFree(IntPtr bo, IntPtr fbHandle) => LibDrm.drmModeRmFB(_card, fbHandle.ToInt32());

		public override unsafe void Dispose()
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			if (DRMDisplayPower.Renderer == this)
			{
				DRMDisplayPower.Renderer = null;
			}
			FrameBufferGpu.Detach(this);
			_busyIndicator?.Dispose();
			// Wake the render loop so it exits
			_renderRequested.Set();
			_pageFlipDone.Set();

			if (_crtcRestored)
			{
				return;
			}
			_crtcRestored = true;

			try
			{
				var connectorId = _savedConnectorId;
				int restoreRes;
				if (_savedCrtc.mode_valid != 0 && connectorId != 0)
				{
					fixed (LibDrm.drmModeModeInfo* modePtr = &_savedCrtc.mode)
					{
						restoreRes = LibDrm.drmModeSetCrtc(_card, _crtc, _savedCrtc.buffer_id, _savedCrtc.x, _savedCrtc.y, &connectorId, 1, modePtr);
					}
				}
				else
				{
					// Nothing was driving the CRTC before us: disable it so the driver releases it.
					restoreRes = LibDrm.drmModeSetCrtc(_card, _crtc, 0, 0, 0, null, 0, null);
				}

				if (restoreRes != 0)
				{
					this.LogDebug()?.Debug($"{nameof(LibDrm.drmModeSetCrtc)} returned {restoreRes} while restoring the original CRTC state on exit.");
				}
			}
			catch (Exception e)
			{
				this.LogDebug()?.Debug($"Failed to restore the original CRTC state on exit: {e.Message}");
			}
		}

		[GeneratedRegex("card[0-9]+")]
		private static partial Regex DRMCardPathRegex();
	}
}
