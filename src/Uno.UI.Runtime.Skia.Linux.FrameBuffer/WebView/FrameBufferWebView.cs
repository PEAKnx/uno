// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
#nullable enable
using System;
using Microsoft.Web.WebView2.Core;
using Uno.UI.Runtime.Skia.Wpe;
using Uno.UI.Xaml.Controls;

namespace Uno.UI.Runtime.Skia;

/// <summary>What the focused editable element of a web page expects (WebKitInputPurpose).</summary>
public enum FrameBufferWebViewInputPurpose
{
	FreeForm,
	Digits,
	Number,
	Phone,
	Url,
	Email,
	Password,
}

/// <summary>
/// An editable element of a web page gained the focus: the app shows its on-screen keyboard and answers with
/// <see cref="Accept"/> or <see cref="Cancel"/>. Raised on the UI thread.
/// </summary>
public sealed class FrameBufferWebViewTextInputRequest
{
	private readonly Action<string> _accept;
	private readonly Action _cancel;
	private bool _done;

	internal FrameBufferWebViewTextInputRequest(FrameBufferWebViewInputPurpose purpose, string value, Action<string> accept, Action cancel)
	{
		Purpose = purpose;
		Value = value;
		_accept = accept;
		_cancel = cancel;
	}

	/// <summary>What the element expects. Passwords are masked by the keyboard.</summary>
	public FrameBufferWebViewInputPurpose Purpose { get; }

	/// <summary>Current text of the element, the keyboard's start value. Do not log it.</summary>
	public string Value { get; }

	/// <summary>
	/// The page ended editing itself (the element lost focus or was removed) or the view went away: the keyboard should
	/// close. Raised on the UI thread.
	/// </summary>
	public event Action? Ended;

	/// <summary>Replaces the whole value of the element with <paramref name="text"/> and presses Return (Enter of the keyboard).</summary>
	public void Accept(string text)
	{
		if (!_done)
		{
			_done = true;
			_accept(text);
		}
	}

	/// <summary>Leaves the value as it was and ends editing.</summary>
	public void Cancel()
	{
		if (!_done)
		{
			_done = true;
			_cancel();
		}
	}

	internal void End()
	{
		_done = true;
		Ended?.Invoke();
	}
}

/// <summary>
/// Web content on the FrameBuffer host: WPE WebKit renders into a texture that Skia composites, so XAML above the
/// web view blends correctly. Provides the native WebView of <c>WebView2</c> when the engine is installed.
/// </summary>
public static class FrameBufferWebView
{
	/// <summary>
	/// True if WPE WebKit is installed and the host renders with the GPU (valid once the window exists). Checks for the
	/// libraries once and never loads WebKit into the process otherwise.
	/// </summary>
	public static bool IsAvailable => WpeRuntime.IsInstalled && FrameBufferGpu.IsAvailable && WpeRuntime.Failure is null;

	/// <summary>Frames the web views have exported so far, for frame rate diagnostics.</summary>
	public static long FrameCount => WpeView.TotalFrames;

	/// <summary>Application name of the user agent (<c>AppleWebKit/... Name/Version</c>), for views created afterwards.</summary>
	public static string? ApplicationName { get; set; }

	/// <summary>Application version of the user agent.</summary>
	public static string? ApplicationVersion { get; set; }

	/// <summary>Raised on the UI thread when a page starts editing text. See <see cref="FrameBufferWebViewTextInputRequest"/>.</summary>
	public static event Action<FrameBufferWebViewTextInputRequest>? TextInputRequested;

	/// <summary>Color scheme of all web pages (CSS <c>prefers-color-scheme</c>, form controls). Any thread.</summary>
	public static void SetDarkMode(bool dark) => WpeRuntime.SetDarkMode(dark);

	/// <summary>
	/// Background of all web views where the page has not painted yet (not yet rendered tiles while scrolling, pages
	/// without a background), as <c>WebView2.DefaultBackgroundColor</c> on Windows. Any thread.
	/// </summary>
	public static void SetDefaultBackgroundColor(Windows.UI.Color color) => WpeRuntime.SetBackgroundColor(color.A, color.R, color.G, color.B);

	internal static void RaiseTextInput(FrameBufferWebViewTextInputRequest request) => TextInputRequested?.Invoke(request);

	internal static bool HasTextInputHandler => TextInputRequested is not null;

	/// <summary>Called by the host before the app starts.</summary>
	internal static void Register() => NativeWebViewProviderOverride.Create = CreateProvider;

	private static INativeWebViewProvider? CreateProvider(CoreWebView2 coreWebView2)
	{
		// without the engine the registered provider (X11, absent on this host) answers, and the app shows a placeholder
		return WpeRuntime.IsInstalled && FrameBufferGpu.IsAvailable ? new WpeNativeWebViewProvider(coreWebView2) : null;
	}
}
