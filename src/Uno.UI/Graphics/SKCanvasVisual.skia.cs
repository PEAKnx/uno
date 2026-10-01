// Modified by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
using System;
using System.Numerics;
using Windows.Foundation;
using Microsoft.UI.Composition;
using SkiaSharp;

namespace Uno.UI.Graphics;

internal class SKCanvasVisual(Action<object, Size> renderCallback, Compositor compositor) : SKCanvasVisualBase(renderCallback, compositor)
{
	// Layer alpha for the opacity of this visual and its ancestors (render thread of the compositor only)
	private static readonly SKPaint s_opacityLayerPaint = new();

	internal override SKPath Paint(in PaintingSession session)
	{
		// We save and restore the canvas state ourselves so that the inheritor doesn't accidentally forget to.
		// The callback draws with its own paints, which know nothing of the opacity the other visuals multiply into
		// their brushes (session.Opacity): apply it as a layer, otherwise e.g. SVG images ignore Opacity.
		if (session.Opacity < 1f)
		{
			s_opacityLayerPaint.Color = SKColors.Black.WithAlpha((byte)Math.Round(session.Opacity * 255));
			session.Canvas.SaveLayer(new SKRect(0, 0, Size.X, Size.Y), s_opacityLayerPaint);
		}
		else
		{
			session.Canvas.Save();
		}
		// clipping here guarantees that drawing doesn't get outside the intended area
		session.Canvas.ClipRect(new SKRect(0, 0, Size.X, Size.Y), antialias: true);
		RenderCallback(session.Canvas, Size.ToSize());
		session.Canvas.Restore();

		return null;
	}

	internal override bool CanPaint() => true;
	public override void Invalidate() => Compositor.InvalidateRender(this);
}
