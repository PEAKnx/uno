// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
#nullable enable

using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Uno.UI.Xaml.Controls;

/// <summary>
/// Lets a Skia host provide the native WebView of <see cref="CoreWebView2"/> itself. Registrations through
/// <c>ApiExtensibility</c> cannot do that: the first one wins, and add-ins register while the app loads.
/// </summary>
internal static class NativeWebViewProviderOverride
{
	/// <summary>Returns the provider for a <see cref="CoreWebView2"/>, or null to use the registered one. Throws if the host cannot create a native WebView.</summary>
	internal static System.Func<CoreWebView2, INativeWebViewProvider?>? Create { get; set; }
}
