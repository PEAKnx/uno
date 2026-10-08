// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
using System;
using System.Diagnostics;
using System.Threading;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Dispatching;

namespace Uno.UI.Runtime.Skia
{
	/// <summary>
	/// Busy indicator for a blocked UI thread on the DRM host. Layout, bindings and frame recording all run on the UI
	/// thread, so while it is busy (e.g. creating a page) no frame is recorded and app animations stand still. The
	/// render thread does not depend on it: it draws the last recorded frame again with a spinner on a dark disc on top
	/// (just the size of the spinner). An app that shows its own loading state can force it (<see cref="FrameBufferBusyIndicator"/>)
	/// and hide its spinner, so the two never alternate.
	/// Opt-in: UNO_FRAMEBUFFER_BUSY_INDICATOR=&lt;ms&gt; shows it when the UI thread did not answer for that long;
	/// UNO_FRAMEBUFFER_BUSY_INDICATOR_COLOR=#RRGGBB sets the arc color (a saturated color reads on light and dark pages).
	/// </summary>
	internal sealed class DRMBusyIndicator : IDisposable
	{
		private const string EnvironmentVariable = "UNO_FRAMEBUFFER_BUSY_INDICATOR";
		private const string ColorEnvironmentVariable = "UNO_FRAMEBUFFER_BUSY_INDICATOR_COLOR";
		private static readonly SKColor DefaultColor = new(0x00, 0x78, 0xd4);
		private const int FrameMs = 33;
		private const int ProbeMs = 50;
		// One revolution of the arc
		private const int RevolutionMs = 1100;
		// Disc relative to the shorter display side, just around the ring; drawn a little above the center like the app's
		// spinner above its text
		private const float DiscRadius = 0.15f;
		private const float CenterUp = 0.06f;

		private readonly long _delayTicks;
		private readonly Action _requestRender;
		private readonly Func<bool> _displayOn;
		private readonly Thread _thread;
		private readonly SKPaint _disc = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x1c, 0x1c, 0x1e, 0xf5) };
		private readonly SKPaint _border = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0xff, 0xff, 0xff, 0x28) };
		private readonly SKPaint _track = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0xff, 0xff, 0xff, 0x24) };
		private readonly SKPaint _arc;
		private static volatile DRMBusyIndicator? s_current;
		private static volatile bool s_forced;
		private volatile bool _disposed;
		private volatile bool _showing;
		// Stopwatch ticks of the pending probe (0: none pending); written by the probe thread, cleared on the UI thread
		private long _probeSent;

		private DRMBusyIndicator(int delayMs, SKColor color, Action requestRender, Func<bool> displayOn)
		{
			_delayTicks = delayMs * Stopwatch.Frequency / 1000;
			_requestRender = requestRender;
			_displayOn = displayOn;
			_arc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, Color = color.WithAlpha(0xff) };
			_thread = new Thread(Run) { IsBackground = true, Name = "DRM busy indicator" };
		}

		/// <summary>The running indicator, null if it is not enabled or the host does not use the DRM renderer.</summary>
		internal static DRMBusyIndicator? Current => s_current;

		/// <summary>The app shows its own loading state: the indicator is shown whatever the UI thread does.</summary>
		internal static void SetForced(bool forced)
		{
			s_forced = forced;
			s_current?._requestRender();
		}

		/// <summary>True while the indicator is drawn on top of the frames (the renderer presents every frame then).</summary>
		public bool IsShowing => _showing;

		/// <summary>Starts the indicator if enabled; null otherwise. <paramref name="requestRender"/> asks the render thread for a frame.</summary>
		public static DRMBusyIndicator? TryStart(Action requestRender, Func<bool> displayOn)
		{
			if (!int.TryParse(Environment.GetEnvironmentVariable(EnvironmentVariable), out var delayMs) || delayMs <= 0)
			{
				return null;
			}
			var color = SKColor.TryParse(Environment.GetEnvironmentVariable(ColorEnvironmentVariable), out var parsed) ? parsed : DefaultColor;
			var indicator = new DRMBusyIndicator(delayMs, color, requestRender, displayOn);
			s_current = indicator;
			indicator._thread.Start();
			indicator.LogInfo()?.Info($"Busy indicator after {delayMs} ms of a blocked UI thread (drawn by the render thread).");
			return indicator;
		}

		/// <summary>
		/// Draws the indicator into a canvas of <paramref name="width"/> x <paramref name="height"/> pixels, rotated like the UI
		/// (<paramref name="degrees"/>, translation as the renderer applies it).
		/// </summary>
		public void Draw(SKCanvas canvas, int width, int height, int degrees, int transX, int transY)
		{
			if (!_showing)
			{
				return;
			}
			// UI orientation: portrait rotations swap the sides
			var uiWidth = degrees is 90 or -90 ? height : width;
			var uiHeight = degrees is 90 or -90 ? width : height;
			var shorter = Math.Min(uiWidth, uiHeight);
			var radius = shorter * DiscRadius;
			var cx = uiWidth / 2f;
			var cy = uiHeight / 2f - shorter * CenterUp;
			var stroke = radius * 0.165f;
			var ring = radius * 0.79f;
			var phase = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency % RevolutionMs * 360f / RevolutionMs;

			canvas.Save();
			canvas.Translate(transX, transY);
			canvas.RotateDegrees(degrees);
			canvas.DrawCircle(cx, cy, radius, _disc);
			_border.StrokeWidth = Math.Max(1, radius / 50);
			canvas.DrawCircle(cx, cy, radius - _border.StrokeWidth / 2, _border);
			_track.StrokeWidth = stroke;
			canvas.DrawCircle(cx, cy, ring, _track);
			_arc.StrokeWidth = stroke;
			canvas.DrawArc(new SKRect(cx - ring, cy - ring, cx + ring, cy + ring), phase - 90, 100, false, _arc);
			canvas.Restore();
		}

		private void Run()
		{
			while (!_disposed)
			{
				var now = Stopwatch.GetTimestamp();
				var sent = Interlocked.Read(ref _probeSent);
				if (sent == 0)
				{
					Interlocked.Exchange(ref _probeSent, now);
					NativeDispatcher.Main.Enqueue(() => Interlocked.Exchange(ref _probeSent, 0), NativeDispatcherPriority.High);
				}

				var busy = (s_forced || (sent != 0 && now - sent >= _delayTicks)) && _displayOn();
				if (busy)
				{
					_showing = true;
					// A frame per step: the render thread draws the last recorded frame again, the UI thread is not involved
					_requestRender();
					Thread.Sleep(FrameMs);
				}
				else
				{
					if (_showing)
					{
						_showing = false;
						// One more frame without the indicator
						_requestRender();
					}
					Thread.Sleep(ProbeMs);
				}
			}
		}

		public void Dispose()
		{
			_disposed = true;
			if (ReferenceEquals(s_current, this))
			{
				s_current = null;
			}
			if (_thread.IsAlive)
			{
				_thread.Join(500);
			}
			_showing = false;
			_disc.Dispose();
			_border.Dispose();
			_track.Dispose();
			_arc.Dispose();
		}
	}
}

namespace Uno.UI.Runtime.Skia
{
	/// <summary>
	/// The busy indicator of the DRM host (spinner on a disc, drawn by the render thread) for apps that want it as their
	/// loading indication: while forced it is shown even though the UI thread is responsive, so the app can hide its own
	/// spinner and the two never alternate. Needs UNO_FRAMEBUFFER_BUSY_INDICATOR (it sets the delay for a blocked UI thread).
	/// </summary>
	public static class FrameBufferBusyIndicator
	{
		/// <summary>True if the indicator is enabled and drawn by the DRM renderer.</summary>
		public static bool IsAvailable => DRMBusyIndicator.Current is not null;

		/// <summary>Shows the indicator until it is switched off again (any thread); no effect when it is not available.</summary>
		public static void SetForced(bool forced) => DRMBusyIndicator.SetForced(forced);
	}
}
