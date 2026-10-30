// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Uno.Foundation.Logging;
using Uno.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace Uno.UI.Runtime.Skia.Wpe;

internal sealed class WpeNativeWebViewProvider(CoreWebView2 coreWebView2) : INativeWebViewProvider
{
	INativeWebView INativeWebViewProvider.CreateNativeWebView(ContentPresenter contentPresenter)
	{
		if (WpeRuntime.Failure is { } failure)
		{
			throw new PlatformNotSupportedException("WPE WebKit cannot be used: " + failure);
		}
		return new WpeNativeWebView(coreWebView2, contentPresenter);
	}
}

/// <summary>The <see cref="INativeWebView"/> of the FrameBuffer host: a <see cref="WpeView"/> shown by a <see cref="WpeCanvasElement"/>.</summary>
internal sealed class WpeNativeWebView : ICleanableNativeWebView
{
	private readonly CoreWebView2 _coreWebView;
	private readonly ContentPresenter _presenter;
	private readonly DispatcherQueue _dispatcher;
	private readonly WpeView _view;
	private bool _dontRaiseNextNavigationCompleted;
	private bool _scrollingEnabled = true;
	private string? _lastFocusValue;
	private int _focusSequence;
	private FrameBufferWebViewTextInputRequest? _openRequest;

	public WpeNativeWebView(CoreWebView2 coreWebView2, ContentPresenter presenter)
	{
		_coreWebView = coreWebView2;
		_presenter = presenter;
		_dispatcher = presenter.DispatcherQueue;
		// the scale is known here: WebKit does not pick up a later change of the device scale factor reliably
		_view = new WpeView(800, 600, (float)(presenter.XamlRoot?.RasterizationScale ?? 1), null);
		// weak: the view lives until it is disposed, the finalizer of this object disposes it
		var weak = new WeakReference<WpeNativeWebView>(this);
		_view.NavigationStarted += uri => { if (weak.TryGetTarget(out var t)) t.OnNavigationStarted(uri); };
		_view.NavigationFinished += (uri, ok, back, fwd) => { if (weak.TryGetTarget(out var t)) t.OnNavigationFinished(uri, ok, back, fwd); };
		_view.TitleChanged += () => { if (weak.TryGetTarget(out var t)) t._dispatcher.TryEnqueue(t._coreWebView.OnDocumentTitleChanged); };
		_view.TextInputFocus += (f, p, h) => { if (weak.TryGetTarget(out var t)) t.OnTextInputFocus(f, p, h); };
		_view.TextInputSurrounding += value => { if (weak.TryGetTarget(out var t)) t._lastFocusValue = value; };
		presenter.Content = new WpeCanvasElement(_view);
	}

	~WpeNativeWebView() => _view.Dispose();

	public string DocumentTitle => _view.Title;

	public void OnLoaded() => _view.SetVisible(true);

	public void OnUnloaded() => _view.SetVisible(false);

	public void GoBack() => _view.GoBack();

	public void GoForward() => _view.GoForward();

	public void Stop() => _view.Stop();

	public void Reload() => _view.Reload();

	public void ProcessNavigation(Uri uri)
	{
		if (_coreWebView.HostToFolderMap.TryGetValue(uri.Host.ToLowerInvariant(), out var folderName))
		{
			_view.Load($"file://{Path.Join(Package.Current.InstalledPath, folderName, uri.PathAndQuery)}");
		}
		else
		{
			_view.Load(uri.ToString());
		}
	}

	public void ProcessNavigation(string html) => _view.LoadHtml(html);

	public void ProcessNavigation(HttpRequestMessage httpRequestMessage)
	{
		var uri = httpRequestMessage.RequestUri?.ToString();
		if (uri is null)
		{
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error($"{nameof(ProcessNavigation)} received an {nameof(HttpRequestMessage)} without a uri.");
			}
			return;
		}
		if (httpRequestMessage.Method != HttpMethod.Get && this.Log().IsEnabled(LogLevel.Warning))
		{
			this.Log().Warn($"Only GET is supported, {httpRequestMessage.Method} was sent as GET.");
		}
		_view.LoadRequest(uri, httpRequestMessage);
	}

	public Task<string?> ExecuteScriptAsync(string script, CancellationToken token) => _view.ExecuteScriptAsync(script);

	public Task<string?> InvokeScriptAsync(string script, string[]? arguments, CancellationToken token)
	{
		// the arguments are JSON-escaped and joined with commas; [1..^1] removes the brackets
		var argumentString = arguments is not null ? JsonSerializer.Serialize(arguments)[1..^1] : "";
		return ExecuteScriptAsync($"{script}({argumentString})", token);
	}

	public void SetScrollingEnabled(bool isScrollingEnabled)
	{
		_scrollingEnabled = isScrollingEnabled;
		ApplyScrolling();
	}

	private void ApplyScrolling()
	{
		var overflow = _scrollingEnabled ? "" : "hidden";
		_ = _view.ExecuteScriptAsync($"document.documentElement.style.overflow='{overflow}';document.body&&(document.body.style.overflow='{overflow}')");
	}

	// ---- navigation events (GLib thread -> UI thread) ----------------------------------------------------------------

	private void OnNavigationStarted(string uriString)
	{
		if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
		{
			return;
		}
		_dispatcher.TryEnqueue(() =>
		{
			_coreWebView.RaiseNavigationStarting(uri, out var cancel);
			if (cancel)
			{
				_view.Stop();
			}
		});
	}

	private void OnNavigationFinished(string uriString, bool success, bool canGoBack, bool canGoForward)
	{
		if (!success)
		{
			_dontRaiseNextNavigationCompleted = true;
		}
		else if (_dontRaiseNextNavigationCompleted)
		{
			// a failed load is followed by the load of WebKit's error page
			_dontRaiseNextNavigationCompleted = false;
			return;
		}
		Uri.TryCreate(uriString, UriKind.Absolute, out var uri);
		_dispatcher.TryEnqueue(() =>
		{
			if (success)
			{
				_coreWebView.SetHistoryProperties(canGoBack, canGoForward);
				_coreWebView.RaiseHistoryChanged();
				if (!_scrollingEnabled)
				{
					ApplyScrolling();
				}
			}
			_coreWebView.RaiseNavigationCompleted(uri, isSuccess: success, httpStatusCode: success ? 200 : 0,
				errorStatus: CoreWebView2WebErrorStatus.Unknown, shouldSetSource: true);
		});
	}

	// ---- text input --------------------------------------------------------------------------------------------------

	private void OnTextInputFocus(bool focused, int purpose, int hints)
	{
		var sequence = Interlocked.Increment(ref _focusSequence);
		if (!focused)
		{
			_dispatcher.TryEnqueue(() =>
			{
				_openRequest?.End();
				_openRequest = null;
			});
			return;
		}
		if (!FrameBufferWebView.HasTextInputHandler)
		{
			return;
		}
		// WebKit reports the text of the element right after the focus: wait briefly for it as the keyboard's start value
		_lastFocusValue = string.Empty;
		Task.Delay(120).ContinueWith(_ =>
		{
			if (sequence != Volatile.Read(ref _focusSequence))
			{
				return;
			}
			var value = _lastFocusValue ?? string.Empty;
			_dispatcher.TryEnqueue(() =>
			{
				var kind = Enum.IsDefined(typeof(FrameBufferWebViewInputPurpose), purpose) ? (FrameBufferWebViewInputPurpose)purpose : FrameBufferWebViewInputPurpose.FreeForm;
				var request = new FrameBufferWebViewTextInputRequest(kind, value, _view.ReplaceText, _view.Blur);
				_openRequest = request;
				FrameBufferWebView.RaiseTextInput(request);
			});
		});
	}
}
