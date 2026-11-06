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
	// contacts that began on this element
	private readonly System.Collections.Generic.HashSet<uint> _pressed = new();

	public WpeCanvasElement(WpeView view)
	{
		_view = view;
		Subscribe(view);
		// The page gets every gesture (as WebView2 on Windows): an ancestor ScrollViewer or FlipView would take over a
		// touch drifting sideways, the contact ended for WebKit mid-gesture and scrolling stopped until the next touch
		ManipulationMode = ManipulationModes.None;
		Loaded += (_, _) => _scale = XamlRoot?.RasterizationScale ?? 1;
		PointerPressed += (s, e) => OnPointer(e, 0);
		PointerMoved += (s, e) => OnPointer(e, 1);
		PointerReleased += (s, e) => OnPointer(e, 2);
		PointerCanceled += (s, e) => OnPointer(e, 2);
		// a lost capture ends the contact too: a contact WebKit never sees end blocks taps and scrolling
		PointerCaptureLost += (s, e) =>
		{
			if (_pressed.Contains(e.Pointer.PointerId))
			{
				OnPointer(e, 2);
			}
		};
		PointerWheelChanged += OnWheel;
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
		var width = (uint)Math.Max(1, area.Width);
		var height = (uint)Math.Max(1, area.Height);
		_view.SetSize(width, height, (float)_scale);
		if (_view.Image is { } image)
		{
			// After a resize the newest frame still has the old size and would be drawn stretched: draw nothing until the engine
			// delivers a frame in the new size (frame size = view size x scale). Safety net: draw it after a while anyway.
			var matches = Math.Abs(image.Width - width * _scale) <= 2 && Math.Abs(image.Height - height * _scale) <= 2;
			if (matches)
			{
				_mismatchSince = 0;
			}
			else if (_mismatchSince == 0)
			{
				_mismatchSince = Environment.TickCount64;
				DispatcherQueue?.TryEnqueue(() => _ = RedrawAfterGraceAsync());
			}
			if (matches || Environment.TickCount64 - _mismatchSince > MismatchGraceMs)
			{
				canvas.DrawImage(image, new SKRect(0, 0, (float)area.Width, (float)area.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
			}
		}
	}

	private const long MismatchGraceMs = 1000;
	private long _mismatchSince;

	// The canvas only redraws on a new frame: a single wake-up makes sure the safety net draws even if the engine stays silent
	private async System.Threading.Tasks.Task RedrawAfterGraceAsync()
	{
		await System.Threading.Tasks.Task.Delay((int)MismatchGraceMs + 50);
		Invalidate();
	}

	private void OnPointer(PointerRoutedEventArgs e, int phase)
	{
		var point = e.GetCurrentPoint(this);
		var touch = e.Pointer.PointerDeviceType == PointerDeviceType.Touch;
		var id = e.Pointer.PointerId;
		var firstContact = false;
		if (phase == 0)
		{
			firstContact = _pressed.Count == 0;
			_pressed.Add(id);
			CapturePointer(e.Pointer);
		}
		else if (phase == 2)
		{
			if (!_pressed.Remove(id))
			{
				// release of a contact that began elsewhere (the tap that opened this view): WebKit never saw its start
				e.Handled = true;
				return;
			}
			ReleasePointerCapture(e.Pointer);
		}
		// input is in device pixels, the view size in DIPs
		_view.Pointer(touch, (int)id, (int)(point.Position.X * _scale), (int)(point.Position.Y * _scale), phase,
			point.Properties.IsLeftButtonPressed, firstContact);
		e.Handled = true;
	}

	private void OnWheel(object sender, PointerRoutedEventArgs e)
	{
		var point = e.GetCurrentPoint(this);
		_view.Wheel((int)(point.Position.X * _scale), (int)(point.Position.Y * _scale), point.Properties.MouseWheelDelta > 0 ? 1 : -1);
		e.Handled = true;
	}
}
