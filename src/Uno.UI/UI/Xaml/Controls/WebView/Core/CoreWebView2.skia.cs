// Modified by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Uno.Foundation.Extensibility;
using Uno.UI.Xaml.Controls;

namespace Microsoft.Web.WebView2.Core;

public partial class CoreWebView2
{
	internal INativeWebView? GetNativeWebViewFromTemplate()
	{
		if (VisualTreeHelper.GetChild((DependencyObject)_owner, 0) is not ContentPresenter { Name: "WebViewTemplateRoot" } contentPresenter)
		{
			return null;
		}

		// PEAKnx: a provider set by the host wins over the attribute-registered ones (the X11 add-in registers for every Linux host)
		if (NativeWebViewProviderOverride.Create?.Invoke(this) is { } overrideProvider)
		{
			return overrideProvider.CreateNativeWebView(contentPresenter);
		}

		if (ApiExtensibility.CreateInstance<INativeWebViewProvider>(this, out var nativeWebViewProvider))
		{
			return nativeWebViewProvider.CreateNativeWebView(contentPresenter);
		}

		return null;
	}
}
