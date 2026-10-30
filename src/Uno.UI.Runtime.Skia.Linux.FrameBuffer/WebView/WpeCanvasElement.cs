// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
#nullable enable
using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace Uno.UI.Runtime.Skia.Wpe;

/// <summary>
/// Skia element that shows the latest frame of a <see cref="WpeView"/> and forwards pointer, touch and wheel input.
/// The canvas only records: the frame was imported on the render thread before (<see cref="WpeView"/>).
/// </summary>
internal sealed class WpeCanvasElement : SKCanvasElement
{
	private WpeView _view;
	private double _scale = 1;

	public WpeCanvasElement(WpeView view)
	{
		_view = view;
		Subscribe(view);
		Loaded += (_, _) => _scale = XamlRoot?.RasterizationScale ?? 1;
		PointerPressed += (s, e) => OnPointer(e, 0);
		PointerMoved += (s, e) => OnPointer(e, 1);
		PointerReleased += (s, e) => OnPointer(e, 2);
		PointerCanceled += (s, e) => OnPointer(e, 2);
		PointerWheelChanged += OnWheel;
	}

	/// <summary>Shows another view (a released view was created again).</summary>
	public void SetView(WpeView view)
	{
		_view = view;
		Subscribe(view);
		Invalidate();
	}

	private void Subscribe(WpeView view)
	{
		var weak = new WeakReference<WpeCanvasElement>(this);
		view.FrameReady += () => { if (weak.TryGetTarget(out var t)) t.DispatcherQueue?.TryEnqueue(t.Invalidate); };
	}

	protected override void RenderOverride(SKCanvas canvas, Size area)
	{
		_scale = XamlRoot?.RasterizationScale ?? _scale;
		// logical (DIP) size: the device scale factor is set separately
		_view.SetSize((uint)Math.Max(1, area.Width), (uint)Math.Max(1, area.Height), (float)_scale);
		if (_view.Image is { } image)
		{
			canvas.DrawImage(image, new SKRect(0, 0, (float)area.Width, (float)area.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
		}
	}

	private void OnPointer(PointerRoutedEventArgs e, int phase)
	{
		var point = e.GetCurrentPoint(this);
		var touch = e.Pointer.PointerDeviceType == PointerDeviceType.Touch;
		if (phase == 0)
		{
			CapturePointer(e.Pointer);
		}
		else if (phase == 2)
		{
			ReleasePointerCapture(e.Pointer);
		}
		// input is in device pixels, the view size in DIPs
		_view.Pointer(touch, (int)e.Pointer.PointerId, (int)(point.Position.X * _scale), (int)(point.Position.Y * _scale), phase,
			point.Properties.IsLeftButtonPressed);
		e.Handled = true;
	}

	private void OnWheel(object sender, PointerRoutedEventArgs e)
	{
		var point = e.GetCurrentPoint(this);
		_view.Wheel((int)(point.Position.X * _scale), (int)(point.Position.Y * _scale), point.Properties.MouseWheelDelta > 0 ? 1 : -1);
		e.Handled = true;
	}
}
