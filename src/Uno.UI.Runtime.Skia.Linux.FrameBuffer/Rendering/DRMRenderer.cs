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
		// Split mode (UNO_FRAMEBUFFER_RENDER_NODE): GBM/EGL render on this GPU render node, the buffers are imported
		// into the display card (_card) for scanout. -1: the display card renders as well.
		private int _renderFd = -1;
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

			_card = OpenDisplayCard(drmInitOptions.CardPath);
			try
			{
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

				var renderNode = Environment.GetEnvironmentVariable("UNO_FRAMEBUFFER_RENDER_NODE");
				if (!string.IsNullOrEmpty(renderNode))
				{
					_renderFd = Libc.open(renderNode, Libc.O_RDWR, 0);
					if (_renderFd == -1)
					{
						throw new InvalidOperationException($"Couldn't open the render node {renderNode} ({Marshal.GetLastWin32Error()})");
					}
					this.LogInfo()?.Info($"Rendering on {renderNode}, scanout on the display card");
				}
				var device = LibDrm.gbm_create_device(_renderFd != -1 ? _renderFd : _card);
				if (device == IntPtr.Zero)
				{
					throw new InvalidOperationException($"{nameof(LibDrm.gbm_create_device)} failed");
				}
				_gbmTargetSurface = LibDrm.gbm_surface_create(device, modeInfo.Resolution.Width, modeInfo.Resolution.Height, drmInitOptions.GBMSurfaceColorFormat.ToInt(), _renderFd != -1
					// Not allocated for scanout: the display card imports the buffer, which must be linear for it
					? LibDrm.GbmBoFlags.GBM_BO_USE_RENDERING | LibDrm.GbmBoFlags.GBM_BO_USE_LINEAR
					: LibDrm.GbmBoFlags.GBM_BO_USE_SCANOUT | LibDrm.GbmBoFlags.GBM_BO_USE_RENDERING);
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
					this.Log().Info($"Found EGL version {major}.{minor}, {_samples} samples, {_stencil} stencil bits.");
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
				// Needs cursor buffers of the display card: not available while another device renders
				_busyIndicator = _renderFd != -1 ? null : DRMBusyIndicator.TryStart(_card, _crtc, device, modeInfo.Resolution.Width, modeInfo.Resolution.Height, () =>
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

				// Diagnostics: UNO_FRAMEBUFFER_GR_OPTIONS=nostencil,nopathcache
				var grOptions = Environment.GetEnvironmentVariable("UNO_FRAMEBUFFER_GR_OPTIONS") ?? "";
				var context = GRContext.CreateGl(glInterface, new GRContextOptions
				{
					AvoidStencilBuffers = grOptions.Contains("nostencil"),
					AllowPathMaskCaching = !grOptions.Contains("nopathcache"),
					BufferMapThreshold = grOptions.Contains("nomap") ? int.MaxValue : -1,
				});
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
			catch
			{
				// The fallback to software rendering must not keep the card open (this process would stay DRM master)
				_busyIndicator?.Dispose();
				if (DRMDisplayPower.Renderer == this)
				{
					DRMDisplayPower.Renderer = null;
				}
				FrameBufferGpu.Detach(this);
				_selfHandle.Free();
				Libc.close(_card);
				if (_renderFd != -1)
				{
					Libc.close(_renderFd);
				}
				throw;
			}
		}

		/// <summary>
		/// Opens <paramref name="path"/>, or the first /dev/dri/card* with a connected connector that has a mode and an
		/// encoder: cards without a display (a GPU-only card, HDMI without a monitor) are skipped, with the reason logged.
		/// </summary>
		private int OpenDisplayCard(string? path)
		{
			if (path is not null)
			{
				var fd = Libc.open(path, Libc.O_RDWR, 0);
				if (fd == -1)
				{
					var errno = Marshal.GetLastWin32Error();
					var errorString = Marshal.PtrToStringAnsi(Libc.strerror(errno));
					_selfHandle.Free();
					throw new InvalidOperationException($"Couldn't open {path} ({errno}): {errorString}");
				}
				this.LogInfo()?.Info($"Found DRM device {path}");
				return fd;
			}

			var reasons = new System.Collections.Generic.List<string>();
			foreach (var file in Directory.GetFiles("/dev/dri/").Where(f => DRMCardPathRegex().IsMatch(Path.GetFileName(f))).OrderBy(f => f, StringComparer.Ordinal))
			{
				var fd = Libc.open(file, Libc.O_RDWR, 0);
				if (fd == -1)
				{
					var errno = Marshal.GetLastWin32Error();
					reasons.Add($"{file}: cannot open ({errno}: {Marshal.PtrToStringAnsi(Libc.strerror(errno))})");
					continue;
				}

				string? reason;
				try
				{
					reason = DescribeUnusable(new DrmResources(fd));
				}
				catch (Exception e)
				{
					reason = $"no DRM resources ({e.Message})";
				}

				if (reason is null)
				{
					this.LogInfo()?.Info($"Found DRM device {file}" + (reasons.Count > 0 ? $" (skipped: {string.Join("; ", reasons)})" : ""));
					return fd;
				}
				reasons.Add($"{file}: {reason}");
				Libc.close(fd);
			}
			_selfHandle.Free();
			throw new FileNotFoundException($"No DRM card with a connected display found ({(reasons.Count > 0 ? string.Join("; ", reasons) : "no /dev/dri/card* device")})");
		}

		// Null if the card drives a connected display, else why not
		private static string? DescribeUnusable(DrmResources resources)
		{
			if (resources.Connectors.Count == 0)
			{
				return "no connectors";
			}
			if (!resources.Connectors.Any(c => c is { Connection: DrmModeConnection.DRM_MODE_CONNECTED, Modes.Count: > 0 }))
			{
				return "no connected connector";
			}
			return resources.Encoders.Count == 0 ? "no encoder" : null;
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
						if (s_trace)
						{
							// Separates the GPU time of the frame from the swap
							using var current = MakeCurrent();
							var finishStart = Stopwatch.GetTimestamp();
							_grContext.Flush(true, true);
							_traceFinish += Stopwatch.GetTimestamp() - finishStart;
						}
						DumpFrame();
						var rendered = Stopwatch.GetTimestamp();
						var bo = SwapBuffers();
						var swapped = Stopwatch.GetTimestamp();
						Interlocked.Add(ref DRMDisplayPower.RenderTicksTotal, swapped - started);
						_pageFlipDone.Wait();
						if (_disposed)
						{
							return;
						}
						var waited = Stopwatch.GetTimestamp();
						PageFlip(bo);
						Interlocked.Increment(ref DRMDisplayPower.PresentedFramesCount);
						if (s_trace)
						{
							TraceFrame(started, rendered, swapped, waited, Stopwatch.GetTimestamp());
						}
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

		// UNO_FRAMEBUFFER_DUMP=<file.png>: the frame about to be presented is saved at most every 2 s (screenshot of the
		// DRM path, which does not write to /dev/fb0)
		private static readonly string? s_dumpPath = Environment.GetEnvironmentVariable("UNO_FRAMEBUFFER_DUMP");
		private long _dumpAt;

		private void DumpFrame()
		{
			if (string.IsNullOrEmpty(s_dumpPath) || _glFbSurface is null || Stopwatch.GetTimestamp() - _dumpAt < 2 * Stopwatch.Frequency)
			{
				return;
			}
			_dumpAt = Stopwatch.GetTimestamp();
			try
			{
				using var current = MakeCurrent();
				Save(_glFbSurface, s_dumpPath);
				if (_surface is { } composition && !ReferenceEquals(composition, _glFbSurface))
				{
					// The retained frame, to tell a wrong composition from a wrong presentation
					Save(composition, Path.ChangeExtension(s_dumpPath, ".retained.png"));
				}
			}
			catch (Exception e)
			{
				this.LogWarn()?.Warn($"Saving the frame to {s_dumpPath} failed: {e.Message}");
			}
		}

		private static void Save(SKSurface surface, string path)
		{
			using var image = surface.Snapshot();
			using var data = image.Encode(SKEncodedImageFormat.Png, 100);
			var temp = path + ".tmp";
			using (var file = File.Create(temp))
			{
				data.SaveTo(file);
			}
			File.Move(temp, path, true);
		}

		// UNO_FRAMEBUFFER_TRACE=1: average time per step of the frames presented in each second, in the log
		private static readonly bool s_trace = Environment.GetEnvironmentVariable("UNO_FRAMEBUFFER_TRACE") == "1";
		private long _traceSince = Stopwatch.GetTimestamp();
		private int _traceFrames;
		private long _traceRender, _traceSwap, _traceFlipWait, _traceFlip, _traceFinish;

		private void TraceFrame(long started, long rendered, long swapped, long waited, long flipped)
		{
			_traceFrames++;
			_traceRender += rendered - started;
			_traceSwap += swapped - rendered;
			_traceFlipWait += waited - swapped;
			_traceFlip += flipped - waited;
			if (flipped - _traceSince < Stopwatch.Frequency)
			{
				return;
			}
			double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency / _traceFrames;
			this.LogInfo()?.Info($"DRM frames: {_traceFrames}/s, render {ms(_traceRender):0.0} ms, swap {ms(_traceSwap):0.0} ms, flip wait {ms(_traceFlipWait):0.0} ms, flip call {ms(_traceFlip):0.0} ms, finish {ms(_traceFinish):0.0} ms");
			_traceSince = flipped;
			_traceFrames = 0;
			_traceRender = _traceSwap = _traceFlipWait = _traceFlip = _traceFinish = 0;
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
			var format = LibDrm.gbm_bo_get_format(bo);
			uint handle;
			if (_renderFd != -1)
			{
				// Buffer of the render device: import it into the display card through a dma-buf
				var prime = LibDrm.gbm_bo_get_fd(bo);
				if (prime < 0)
				{
					throw new InvalidOperationException($"{nameof(LibDrm.gbm_bo_get_fd)} failed");
				}
				var imported = LibDrm.drmPrimeFDToHandle(_card, prime, out handle);
				Libc.close(prime);
				if (imported != 0)
				{
					throw new InvalidOperationException($"{nameof(LibDrm.drmPrimeFDToHandle)} failed ({imported}, errno {Marshal.GetLastWin32Error()})");
				}
			}
			else
			{
				handle = LibDrm.gbm_bo_get_handle(bo).u32;
			}

			// prepare for the new ioctl call
			var handles = new uint[] { handle, 0, 0, 0 };
			var pitches = new uint[] { stride, 0, 0, 0 };
			var offsets = new uint[4];

			var ret = LibDrm.drmModeAddFB2(_card, w, h, format, handles, pitches,
				offsets, out var fbHandle, 0);
			if (ret != 0)
			{
				if (_renderFd != -1)
				{
					LibDrm.drmCloseBufferHandle(_card, handle);
				}
				throw new InvalidOperationException($"{nameof(LibDrm.drmModeAddFB2)} failed {ret}");
			}
			if (_renderFd != -1)
			{
				_importedHandles[fbHandle] = handle;
			}

			LibDrm.gbm_bo_set_user_data(bo, new IntPtr((int)fbHandle), OnBoFree);

			return fbHandle;
		}

		// GEM handles imported into the display card, by framebuffer id
		private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, uint> _importedHandles = new();

		private void OnBoFree(IntPtr bo, IntPtr fbHandle)
		{
			LibDrm.drmModeRmFB(_card, fbHandle.ToInt32());
			if (_importedHandles.TryRemove((uint)fbHandle.ToInt32(), out var handle))
			{
				LibDrm.drmCloseBufferHandle(_card, handle);
			}
		}

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
