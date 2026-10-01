// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Dispatching;

namespace Uno.UI.Runtime.Skia
{
	/// <summary>
	/// Busy indicator for a blocked UI thread on the DRM host. Layout, bindings and frame recording all run on the UI
	/// thread, so while it is busy (e.g. creating a page) no frame is produced and app animations stand still. This
	/// indicator lives on the display's cursor plane: the display engine blends it over the last frame, so it spins
	/// without the UI thread and without touching the frame buffers.
	/// Opt-in: UNO_FRAMEBUFFER_BUSY_INDICATOR=&lt;ms&gt; shows it when the UI thread did not answer for that long;
	/// UNO_FRAMEBUFFER_BUSY_INDICATOR_COLOR=#RRGGBB sets the arc color (a saturated color reads on light and dark pages).
	/// </summary>
	internal sealed class DRMBusyIndicator : IDisposable
	{
		private const string EnvironmentVariable = "UNO_FRAMEBUFFER_BUSY_INDICATOR";
		private const string ColorEnvironmentVariable = "UNO_FRAMEBUFFER_BUSY_INDICATOR_COLOR";
		private static readonly SKColor DefaultColor = new(0x00, 0x78, 0xd4);
		private const int Steps = 30;
		private const int FrameMs = 33;
		private const int ProbeMs = 50;

		private readonly int _card;
		private readonly uint _crtc;
		private readonly int _size;
		private readonly IntPtr[] _bos = new IntPtr[Steps];
		private readonly uint[] _handles = new uint[Steps];
		private readonly long _delayTicks;
		private readonly Func<bool> _displayOn;
		private readonly Thread _thread;
		private volatile bool _disposed;
		// Stopwatch ticks of the pending probe (0: none pending); written by the probe thread, cleared on the UI thread
		private long _probeSent;
		private int _x, _y;

		private DRMBusyIndicator(int card, uint crtc, int size, int delayMs, Func<bool> displayOn)
		{
			_card = card;
			_crtc = crtc;
			_size = size;
			_delayTicks = delayMs * Stopwatch.Frequency / 1000;
			_displayOn = displayOn;
			_thread = new Thread(Run) { IsBackground = true, Name = "DRM busy indicator" };
		}

		/// <summary>Starts the indicator if enabled and the driver has a cursor plane; null otherwise.</summary>
		public static DRMBusyIndicator? TryStart(int card, uint crtc, IntPtr gbmDevice, int displayWidth, int displayHeight, Func<bool> displayOn)
		{
			if (!int.TryParse(Environment.GetEnvironmentVariable(EnvironmentVariable), out var delayMs) || delayMs <= 0)
			{
				return null;
			}

			// About a tenth of the shorter display side in a common cursor size. DRM_CAP_CURSOR_WIDTH is only the size
			// the driver suggests (often 64): larger sizes are tried and the first one the cursor plane takes is kept.
			var wanted = Math.Min(displayWidth, displayHeight) / 10;
			foreach (var size in new[] { 256, 128, 64 })
			{
				if (size != 64 && size > wanted * 3 / 2)
				{
					continue;
				}
				var indicator = new DRMBusyIndicator(card, crtc, size, delayMs, displayOn);
				try
				{
					indicator.CreateFrames(gbmDevice);
					// The cursor plane rejects sizes it cannot scan out; shown off screen, so nothing flashes
					Native.drmModeMoveCursor(card, crtc, -size, -size);
					if (Native.drmModeSetCursor(card, crtc, indicator._handles[0], (uint)size, (uint)size) != 0)
					{
						throw new InvalidOperationException($"the cursor plane does not take {size} px");
					}
					Native.drmModeSetCursor(card, crtc, 0, 0, 0);
				}
				catch (Exception e)
				{
					indicator.LogInfo()?.Info($"Busy indicator: {e.Message}");
					indicator.Dispose();
					continue;
				}
				indicator._x = (displayWidth - size) / 2;
				indicator._y = (displayHeight - size) / 2;
				indicator._thread.Start();
				indicator.LogInfo()?.Info($"Busy indicator after {delayMs} ms of a blocked UI thread ({size} px cursor plane).");
				return indicator;
			}
			return null;
		}

		private unsafe void CreateFrames(IntPtr gbmDevice)
		{
			var info = new SKImageInfo(_size, _size, SKColorType.Bgra8888, SKAlphaType.Premul);
			using var bitmap = new SKBitmap(info);
			using var canvas = new SKCanvas(bitmap);
			var color = SKColor.TryParse(Environment.GetEnvironmentVariable(ColorEnvironmentVariable), out var parsed) ? parsed : DefaultColor;
			float stroke = _size * 0.09f, inset = stroke;
			// Arc on a faint gray track: no background disc, readable on light and dark pages
			using var track = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = new SKColor(0x80, 0x80, 0x80, 0x50) };
			using var arc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, StrokeCap = SKStrokeCap.Round, Color = color.WithAlpha(0xff) };
			var ring = new SKRect(inset, inset, _size - inset, _size - inset);

			for (var i = 0; i < Steps; i++)
			{
				canvas.Clear(SKColors.Transparent);
				canvas.DrawOval(ring, track);
				canvas.DrawArc(ring, i * 360f / Steps - 90, 100, false, arc);
				canvas.Flush();

				var bo = Native.gbm_bo_create(gbmDevice, (uint)_size, (uint)_size, Native.GBM_FORMAT_ARGB8888, Native.GBM_BO_USE_CURSOR | Native.GBM_BO_USE_WRITE);
				if (bo == IntPtr.Zero)
				{
					throw new InvalidOperationException("gbm_bo_create for a cursor buffer failed");
				}
				_bos[i] = bo;
				// Cursor buffers are tightly packed (stride = width * 4), as is the bitmap
				if (Native.gbm_bo_write(bo, bitmap.GetPixels(), (nuint)(_size * _size * 4)) != 0)
				{
					throw new InvalidOperationException("gbm_bo_write to a cursor buffer failed");
				}
				_handles[i] = (uint)Native.gbm_bo_get_handle(bo);
			}
		}

		private void Run()
		{
			var shown = false;
			var step = 0;
			while (!_disposed)
			{
				var now = Stopwatch.GetTimestamp();
				var sent = Interlocked.Read(ref _probeSent);
				if (sent == 0)
				{
					Interlocked.Exchange(ref _probeSent, now);
					NativeDispatcher.Main.Enqueue(() => Interlocked.Exchange(ref _probeSent, 0), NativeDispatcherPriority.High);
				}

				var busy = sent != 0 && now - sent >= _delayTicks && _displayOn();
				if (busy)
				{
					if (!shown)
					{
						Native.drmModeMoveCursor(_card, _crtc, _x, _y);
						shown = true;
					}
					Native.drmModeSetCursor(_card, _crtc, _handles[step], (uint)_size, (uint)_size);
					step = (step + 1) % Steps;
					Thread.Sleep(FrameMs);
				}
				else
				{
					if (shown)
					{
						Native.drmModeSetCursor(_card, _crtc, 0, 0, 0);
						shown = false;
					}
					Thread.Sleep(ProbeMs);
				}
			}
			if (shown)
			{
				Native.drmModeSetCursor(_card, _crtc, 0, 0, 0);
			}
		}

		public void Dispose()
		{
			_disposed = true;
			if (_thread.IsAlive)
			{
				_thread.Join(500);
			}
			for (var i = 0; i < Steps; i++)
			{
				if (_bos[i] != IntPtr.Zero)
				{
					Native.gbm_bo_destroy(_bos[i]);
					_bos[i] = IntPtr.Zero;
				}
			}
		}

		private static class Native
		{
			private const string libdrm = "libdrm.so.2";
			private const string libgbm = "libgbm.so.1";

			// fourcc 'AR24'
			public const uint GBM_FORMAT_ARGB8888 = 0x34325241;
			public const uint GBM_BO_USE_CURSOR = 1 << 1;
			public const uint GBM_BO_USE_WRITE = 1 << 3;

			[DllImport(libdrm)]
			public static extern int drmModeSetCursor(int fd, uint crtcId, uint bo_handle, uint width, uint height);

			[DllImport(libdrm)]
			public static extern int drmModeMoveCursor(int fd, uint crtcId, int x, int y);

			[DllImport(libgbm)]
			public static extern IntPtr gbm_bo_create(IntPtr gbm, uint width, uint height, uint format, uint flags);

			[DllImport(libgbm)]
			public static extern int gbm_bo_write(IntPtr bo, IntPtr buf, nuint count);

			// union gbm_bo_handle; the GEM handle is its u32
			[DllImport(libgbm)]
			public static extern ulong gbm_bo_get_handle(IntPtr bo);

			[DllImport(libgbm)]
			public static extern void gbm_bo_destroy(IntPtr bo);
		}
	}
}
