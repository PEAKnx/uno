using System;
using Uno.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Graphics;
using Windows.UI.Core;
using Uno.Extensions;
using Uno.UI.Dispatching;
using Uno.UI.Runtime.Skia;

namespace Uno.WinUI.Runtime.Skia.Linux.FrameBuffer.UI;

internal class FrameBufferWindowWrapper : NativeWindowWrapperBase
{
	private static FrameBufferWindowWrapper? _instance;
	internal static FrameBufferWindowWrapper Instance => _instance!;

	public static void Init(DisplayOrientations orientation) => _instance = new(orientation);

	public override object? NativeWindow => null;

	private FrameBufferWindowWrapper(DisplayOrientations orientation)
	{
		if (_instance != null)
		{
			throw new InvalidOperationException($"{nameof(FrameBufferWindowWrapper)} should be created once.");
		}
		_instance = this;

		_orientation = orientation;
	}

	// Read by the render and input threads, changed on the UI thread (SetOrientation)
	private volatile DisplayOrientations _orientation;
	private Size? _rawScreenSize;

	public DisplayOrientations Orientation => _orientation;

	/// <summary>
	/// Changes the display orientation at runtime (UI thread): the window gets the rotated size, so the app lays
	/// out for it, and rendering and pointer mapping follow the new orientation.
	/// </summary>
	internal void SetOrientation(DisplayOrientations orientation)
	{
		NativeDispatcher.CheckThreadAccess();
		if (orientation == _orientation)
		{
			return;
		}

		if (_rawScreenSize is { } rawScreenSize && XamlRoot is { })
		{
			ApplySize(rawScreenSize, orientation);
		}
		_orientation = orientation;
	}

	internal void SetSize(Size rawScreenSize)
	{
		_rawScreenSize = rawScreenSize;
		if (XamlRoot is { })
		{
			ApplySize(rawScreenSize, _orientation);
		}
		else
		{
			NativeDispatcher.Main.Enqueue(() => SetSize(rawScreenSize));
		}
	}

	private void ApplySize(Size rawScreenSize, DisplayOrientations orientation)
	{
		var scale = RasterizationScale = (float)DisplayInformation.GetForCurrentViewSafe().RawPixelsPerViewPixel;
		if (orientation is DisplayOrientations.Portrait or DisplayOrientations.PortraitFlipped)
		{
			(rawScreenSize.Height, rawScreenSize.Width) = (rawScreenSize.Width, rawScreenSize.Height);
		}
		var bounds = new Rect(0, 0, rawScreenSize.Width / scale, rawScreenSize.Height / scale);
		SetBoundsAndVisibleBounds(bounds, bounds);
		var fullSize = new SizeInt32((int)rawScreenSize.Width, (int)rawScreenSize.Height);
		SetSizes(fullSize, fullSize);
		FrameBufferPointerInputSource.Instance.MousePosition = bounds.GetCenter();
	}

	internal void OnNativeVisibilityChanged(bool visible) => IsVisible = visible;

	internal void OnNativeActivated(CoreWindowActivationState state) => ActivationState = state;

	internal void OnNativeClosed() => RaiseClosing();
}
