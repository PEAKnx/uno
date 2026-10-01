// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
using Uno.UI.Dispatching;
using Uno.WinUI.Runtime.Skia.Linux.FrameBuffer.UI;
using Windows.Graphics.Display;

namespace Uno.UI.Runtime.Skia;

/// <summary>Display settings of the FrameBuffer host that can change while the app runs.</summary>
public static class FrameBufferDisplay
{
	/// <summary>
	/// Orientation of the display (initially the one given to FramebufferHostBuilder.Orientation). Setting it
	/// rotates rendering and pointer input and resizes the window, e.g. from an orientation sensor. Can be set from
	/// any thread; it is applied on the UI thread.
	/// </summary>
	public static DisplayOrientations Orientation
	{
		get => FrameBufferWindowWrapper.Instance.Orientation;
		set => NativeDispatcher.Main.Enqueue(() => FrameBufferWindowWrapper.Instance.SetOrientation(value));
	}
}
