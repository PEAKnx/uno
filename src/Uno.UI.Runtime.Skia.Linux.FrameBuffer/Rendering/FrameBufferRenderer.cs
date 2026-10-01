using System;
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

	public readonly record struct MouseIndicatorOptions(bool? ShowMouseCursor, float MouseCursorRadius, System.Drawing.Color MouseCursorColor);

	protected FrameBufferRenderer(IXamlRootHost host, MouseIndicatorOptions mouseIndicatorOptions)
	{
		_host = host;
		_cursorPaint = new SKPaint { Color = mouseIndicatorOptions.MouseCursorColor.ToSKColor() };
		_cursorRadius = mouseIndicatorOptions.MouseCursorRadius;
		_cursorVisible = mouseIndicatorOptions.ShowMouseCursor;
		_receivedMouseEvent = FrameBufferPointerInputSource.Instance.ReceivedMouseEvent;
		FrameBufferPointerInputSource.Instance.MouseEventReceived += OnMouseEventReceived;
	}

	private void OnMouseEventReceived()
	{
		FrameBufferPointerInputSource.Instance.MouseEventReceived -= OnMouseEventReceived;
		_receivedMouseEvent = true;
	}

	protected void Render()
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
		// Full redraw every frame: CompositionTarget clips the frame to the damage region when it gets the
		// previous canvas, assuming the target still holds the last frame. The GBM buffers rotate (the back
		// buffer holds an older frame), and the complex damage clip is expensive on weak GPUs. A null canvas
		// makes it treat every frame as new; the surface itself is reused while the size stays the same.
		ct.OnNativePlatformFrameRequested(null, size =>
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
			}
			_surface.Canvas.Save();
			_surface.Canvas.Translate((float)transX, (float)transY);
			_surface.Canvas.RotateDegrees(degrees);
			return _surface.Canvas;
		});
		_surface?.Canvas.Restore();
		_surface?.Flush();

		PresentToOutput(degrees, transX, transY);
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

	public virtual void Dispose() { }
}
