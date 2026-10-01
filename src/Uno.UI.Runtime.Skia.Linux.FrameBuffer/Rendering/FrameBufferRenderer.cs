// Modified by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Windows.Graphics.Display;
using Windows.Graphics.Interop.Direct2D;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;
using Uno.WinUI.Runtime.Skia.Linux.FrameBuffer.UI;
using Size = Windows.Foundation.Size;

namespace Uno.UI.Runtime.Skia;

internal abstract class FrameBufferRenderer
{
	protected readonly IXamlRootHost _host;
	private readonly SKPaint _cursorPaint;
	private readonly float _cursorRadius;
	private readonly bool? _cursorVisible;
	protected SKSurface? _surface;
	private Size _surfaceSize;
	private int _renderCount;
	private bool _receivedMouseEvent;
	// CompositionTarget draws its last frame again on every native frame request (the request is what schedules
	// the next recording), also when nothing was recorded since: such a frame is not presented again
	private object? _presentedFrame;
	private Windows.Foundation.Point? _presentedCursor;
	// Partial redraw: geometry of the retained frame, and the frame to redraw completely until a newer one was drawn
	private (Windows.Graphics.SizeInt32 Bounds, DisplayOrientations Orientation, float Scale) _geometry;
	private object? _redrawUntilNewerThan;
	// The retained frame missed frames drawn straight into the output (DirectSurface): redraw it completely next
	private bool _retainedStale;

	// Damaged share of the screen from which a frame is drawn straight into the output: redrawing nearly everything
	// into the retained frame and copying it costs more than a full redraw (scrolling, fullscreen video)
	private const double DirectRedrawDamage = 0.5;

	public readonly record struct MouseIndicatorOptions(bool? ShowMouseCursor, float MouseCursorRadius, System.Drawing.Color MouseCursorColor);

	protected FrameBufferRenderer(IXamlRootHost host, MouseIndicatorOptions mouseIndicatorOptions)
	{
		_host = host;
		_cursorPaint = new SKPaint { Color = mouseIndicatorOptions.MouseCursorColor.ToSKColor() };
		_cursorRadius = mouseIndicatorOptions.MouseCursorRadius;
		_cursorVisible = mouseIndicatorOptions.ShowMouseCursor;
		_receivedMouseEvent = FrameBufferPointerInputSource.Instance.ReceivedMouseEvent;
		FrameBufferPointerInputSource.Instance.MouseEventReceived += OnMouseEventReceived;
		// Partial redraw needs the damage of each frame; a full redraw never uses it
		Microsoft.UI.Composition.Visual.EnableDamageTracking = PartialRedraw;
		if (!CompositionTargetFrameSlot.IsAvailable)
		{
			this.LogWarn()?.Warn("CompositionTarget frame slot not found: full redraw, every frame is presented.");
		}
	}

	/// <summary>
	/// The renderer keeps the last frame in <see cref="_surface"/> and presents a copy of it: CompositionTarget then
	/// redraws only the damaged region. Otherwise every frame is redrawn completely.
	/// </summary>
	protected virtual bool PartialRedraw => false;

	private void OnMouseEventReceived()
	{
		FrameBufferPointerInputSource.Instance.MouseEventReceived -= OnMouseEventReceived;
		_receivedMouseEvent = true;
	}

	/// <summary>Draws the current frame; returns false when there is nothing new to present.</summary>
	protected bool Render()
	{
		if (this.Log().IsEnabled(LogLevel.Trace))
		{
			this.Log().Trace($"Render {_renderCount++}");
		}

		if (_host.RootElement?.Visual.CompositionTarget is not CompositionTarget ct)
		{
			throw new Exception($"CompositionTarget is not set on the {nameof(IXamlRootHost)} at the point of rendering.");
		}

		using var _ = MakeCurrent();
		var bounds = FrameBufferWindowWrapper.Instance.Size;
		var orientation = FrameBufferWindowWrapper.Instance.Orientation;
		var (degrees, transX, transY) = orientation switch
		{
			DisplayOrientations.None => (0, 0, 0),
			DisplayOrientations.Landscape => (0, 0, 0),
			DisplayOrientations.Portrait => (90, bounds.Height, 0),
			DisplayOrientations.LandscapeFlipped => (180, bounds.Width, bounds.Height),
			DisplayOrientations.PortraitFlipped => (-90, 0, bounds.Width),
			_ => throw new ArgumentOutOfRangeException()
		};
		// The frame CompositionTarget is about to draw, see Present
		var slotBefore = CompositionTargetFrameSlot.Current(ct);
		var recreated = false;

		if (PartialRedraw)
		{
			// After a size, orientation or scale change the retained frame fits no frame recorded for the new layout (their
			// unchanged parts are not in the damage): redraw completely until such a frame was drawn
			var geometry = (bounds, orientation, FrameBufferWindowWrapper.Instance.RasterizationScale);
			if (!_geometry.Equals(geometry))
			{
				_geometry = geometry;
				_redrawUntilNewerThan = slotBefore;
			}
			var scale = FrameBufferWindowWrapper.Instance.RasterizationScale;
			if (DirectSurface is { } direct && slotBefore is not null && !ReferenceEquals(slotBefore, _presentedFrame)
				&& CompositionTargetFrameSlot.DamageFraction(ct, bounds.Width * bounds.Height / (scale * scale)) >= DirectRedrawDamage)
			{
				// Full redraw into the output buffer, no copy; the retained frame is redrawn with the next partial frame
				direct.Canvas.Save();
				ct.OnNativePlatformFrameRequested(null, _ =>
				{
					direct.Canvas.Translate((float)transX, (float)transY);
					direct.Canvas.RotateDegrees(degrees);
					return direct.Canvas;
				});
				direct.Canvas.Restore();
				direct.Flush();
				_retainedStale = true;
				var drawnDirectly = DrawnFrame(ct, slotBefore);
				if (drawnDirectly is not null && !ReferenceEquals(drawnDirectly, _redrawUntilNewerThan))
				{
					_redrawUntilNewerThan = null;
				}
				return Present(drawnDirectly, recreated: true, degrees, transX, transY, toOutput: false);
			}

			if (_retainedStale && _surface is { } retained && slotBefore is not null && ReferenceEquals(slotBefore, _presentedFrame)
				&& CursorPosition == _presentedCursor)
			{
				// The frame on screen was drawn directly and nothing new was recorded: drawn clipped out (the request
				// schedules the next recording); the retained frame is redrawn with the next new frame
				retained.Canvas.Save();
				retained.Canvas.ClipRect(SKRect.Empty);
				var resized = false;
				ct.OnNativePlatformFrameRequested(retained.Canvas, _ =>
				{
					resized = true;
					return retained.Canvas;
				});
				retained.Canvas.Restore();
				if (resized)
				{
					// Drawn clipped out at a new size: draw it on the next pass
					InvalidateRender();
				}
				return false;
			}

			var redraw = _redrawUntilNewerThan is not null || _retainedStale;

			// Otherwise the surface still holds the last frame: CompositionTarget clips this frame to its damage region
			var canvas = redraw ? null : _surface?.Canvas;
			canvas?.Save();
			canvas?.Translate(transX, transY);
			canvas?.RotateDegrees(degrees);
			ct.OnNativePlatformFrameRequested(canvas, size =>
			{
				if (orientation is DisplayOrientations.Portrait or DisplayOrientations.PortraitFlipped)
				{
					size = new Size(size.Height, size.Width);
				}
				if (_surface is null || _surfaceSize != size)
				{
					_surface?.Dispose();
					_surface = UpdateSize((int)size.Width, (int)size.Height);
					_surfaceSize = size;
					recreated = true;
				}
				_surface.Canvas.Save();
				_surface.Canvas.Translate((float)transX, (float)transY);
				_surface.Canvas.RotateDegrees(degrees);
				return _surface.Canvas;
			});
			_surface?.Canvas.Restore();
			_surface?.Flush();

			var drawn = DrawnFrame(ct, slotBefore);
			if (redraw && drawn is not null)
			{
				_retainedStale = false;
				if (!ReferenceEquals(drawn, _redrawUntilNewerThan))
				{
					_redrawUntilNewerThan = null;
				}
			}
			return Present(drawn, recreated || redraw, degrees, transX, transY);
		}

		// Full redraw every frame: CompositionTarget clips the frame to the damage region when it gets the
		// previous canvas, assuming the target still holds the last frame. The GBM buffers rotate (the back
		// buffer holds an older frame). A null canvas makes it treat every frame as new; the surface itself is
		// reused while the size stays the same.
		// Redrawing the frame on screen is wasted: it is drawn clipped out instead (a frame published meanwhile is
		// drawn on the render request that follows its publication). Partial redraw does not need this, as such a
		// frame has no damage left.
		var probe = slotBefore is not null && ReferenceEquals(slotBefore, _presentedFrame) && _surface is not null
			&& CursorPosition == _presentedCursor;
		if (probe)
		{
			_surface!.Canvas.Save();
			_surface.Canvas.ClipRect(SKRect.Empty);
		}
		ct.OnNativePlatformFrameRequested(probe ? _surface!.Canvas : null, size =>
		{
			if (probe)
			{
				// The size changed: draw the frame
				_surface!.Canvas.Restore();
				probe = false;
			}
			if (orientation is DisplayOrientations.Portrait or DisplayOrientations.PortraitFlipped)
			{
				size = new Size(size.Height, size.Width);
			}
			if (_surface is null || _surfaceSize != size)
			{
				_surface?.Dispose();
				_surface = UpdateSize((int)size.Width, (int)size.Height);
				_surfaceSize = size;
				recreated = true;
			}
			_surface.Canvas.Save();
			_surface.Canvas.Translate((float)transX, (float)transY);
			_surface.Canvas.RotateDegrees(degrees);
			return _surface.Canvas;
		});
		_surface?.Canvas.Restore();
		if (probe)
		{
			// Nothing drawn (the output holds an older frame): a moved cursor needs a drawn frame
			if (CursorPosition != _presentedCursor)
			{
				InvalidateRender();
			}
			return false;
		}
		_surface?.Flush();

		return Present(DrawnFrame(ct, slotBefore), recreated, degrees, transX, transY);
	}

	private Windows.Foundation.Point? CursorPosition => ShouldShowCursor ? FrameBufferPointerInputSource.Instance.MousePosition : null;

	// The frame drawn is known when the slot holds the same frame before and after drawing (one published in between
	// would be in the slot after it), otherwise null: then this frame is presented and so is the next one
	private static object? DrawnFrame(CompositionTarget ct, object? slotBefore)
		=> slotBefore is not null && ReferenceEquals(slotBefore, CompositionTargetFrameSlot.Current(ct)) ? slotBefore : null;

	private bool Present(object? drawn, bool recreated, int degrees, int transX, int transY, bool toOutput = true)
	{
		var cursor = CursorPosition;
		if (drawn is not null && ReferenceEquals(drawn, _presentedFrame) && !recreated && cursor == _presentedCursor)
		{
			return false;
		}
		_presentedFrame = drawn;
		_presentedCursor = cursor;

		if (toOutput)
		{
			PresentToOutput(degrees, transX, transY);
		}
		else
		{
			PresentDirect(degrees, transX, transY);
		}
		return true;
	}

	protected bool ShouldShowCursor => _cursorVisible ?? _receivedMouseEvent;

	protected void DrawCursor(SKCanvas outputCanvas, int degrees, int transX, int transY)
	{
		if (!ShouldShowCursor)
		{
			return;
		}

		outputCanvas.Save();
		outputCanvas.Translate(transX, transY);
		outputCanvas.RotateDegrees(degrees);
		outputCanvas.Scale(FrameBufferWindowWrapper.Instance.RasterizationScale);
		outputCanvas.DrawCircle(FrameBufferPointerInputSource.Instance.MousePosition.ToSkia(), _cursorRadius, _cursorPaint);
		outputCanvas.Restore();
	}

	public abstract void InvalidateRender();

	protected abstract IDisposable MakeCurrent();

	protected abstract SKSurface UpdateSize(int width, int height);

	protected abstract void PresentToOutput(int degrees, int transX, int transY);

	/// <summary>
	/// Partial redraw: the output surface a frame can be drawn into directly (no retained copy), null if none.
	/// </summary>
	protected virtual SKSurface? DirectSurface => null;

	/// <summary>Presents a frame drawn into <see cref="DirectSurface"/>.</summary>
	protected virtual void PresentDirect(int degrees, int transX, int transY)
	{
	}

	public virtual void Dispose() { }
}

/// <summary>
/// Identity of the frame CompositionTarget keeps for the next draw (its frame slot, read under its lock), so the host
/// knows whether a draw was a frame it presented already. CompositionTarget has no API for this in this version, so
/// the private fields are read by reflection (pinned Uno version). Without them every frame is presented, as before.
/// </summary>
internal static class CompositionTargetFrameSlot
{
	private static readonly FieldInfo? s_gate = typeof(CompositionTarget).GetField("_frameGate", BindingFlags.NonPublic | BindingFlags.Instance);
	private static readonly FieldInfo? s_slot = typeof(CompositionTarget).GetField("_lastRenderedFrame", BindingFlags.NonPublic | BindingFlags.Instance);
	private static readonly object s_empty = new();

	internal static bool IsAvailable { get; } = s_gate?.FieldType == typeof(Lock) && s_slot is not null;

	/// <summary>The frame in the slot (a placeholder when empty), null if unavailable.</summary>
	internal static object? Current(CompositionTarget target)
	{
		if (!IsAvailable || s_gate!.GetValue(target) is not Lock gate)
		{
			return null;
		}
		object? frame;
		lock (gate)
		{
			frame = s_slot!.GetValue(target);
		}
		return frame is ITuple { Length: > 0 } tuple && tuple[0] is { } picture ? picture : s_empty;
	}

	/// <summary>Share of the frame area (logical units) covered by the damage area of the frame in the slot, 0 if unknown.</summary>
	internal static double DamageFraction(CompositionTarget target, double frameArea)
	{
		if (!IsAvailable || frameArea <= 0 || s_gate!.GetValue(target) is not Lock gate)
		{
			return 0;
		}
		double damage = 0;
		lock (gate)
		{
			if (s_slot!.GetValue(target) is not ITuple { Length: > 2 } tuple || tuple[2] is not SKPath path)
			{
				return 0;
			}
			// Real area, not the bounds: two small rects far apart are a small share
			using var clip = new SKRegion(SKRectI.Ceiling(path.Bounds, true));
			using var region = new SKRegion();
			if (!region.SetPath(path, clip))
			{
				return 0;
			}
			using var rects = region.CreateRectIterator();
			while (rects.Next(out var rect))
			{
				damage += (double)rect.Width * rect.Height;
			}
		}
		return damage / frameArea;
	}
}
