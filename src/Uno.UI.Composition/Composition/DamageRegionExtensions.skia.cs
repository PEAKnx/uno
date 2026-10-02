#nullable enable

using System;
using SkiaSharp;

namespace Uno.UI.Composition;

internal static class DamageRegionExtensions
{
	// Path ops cost grows with the edge count of the region: the damage of a frame that creates many visuals (a new
	// page) and the damage carried over from a frame not presented yet made every further union slower. Beyond this
	// many points the region becomes its bounds, a superset: the frame redraws a little more, never less.
	private const int MaxPoints = 64;

	[ThreadStatic]
	private static SKPath? _unionRectScratch;

	[ThreadStatic]
	private static SKPath? _clampScratch;

	public static void Union(this SKPath region, SKPath addition)
	{
		if (addition.IsEmpty)
		{
			return;
		}

		if (region.IsEmpty)
		{
			region.AddPath(addition);
		}
		else if (region.IsRect && region.Bounds.Contains(addition.Bounds))
		{
			// Covered already
			return;
		}
		else if (addition.IsRect && addition.Bounds.Contains(region.Bounds))
		{
			var bounds = addition.Bounds;
			region.Rewind();
			region.AddRect(bounds);
			return;
		}
		else
		{
			region.Op(addition, SKPathOp.Union, region);
		}

		if (region.PointCount > MaxPoints)
		{
			var bounds = region.Bounds;
			region.Rewind();
			region.AddRect(bounds);
		}
	}

	public static void UnionRect(this SKPath region, SKRect rect)
	{
		if (rect.IsEmpty)
		{
			return;
		}

		if (region.IsRect && region.Bounds.Contains(rect))
		{
			return;
		}

		var scratch = _unionRectScratch ??= new SKPath();
		scratch.Rewind();
		scratch.AddRect(rect);
		region.Union(scratch);
	}

	public static void ClampTo(this SKPath region, SKRect frameRect)
	{
		if (region.IsEmpty || frameRect.Contains(region.Bounds))
		{
			return;
		}

		if (region.IsRect)
		{
			var clamped = region.Bounds;
			if (!clamped.IntersectsWith(frameRect))
			{
				region.Rewind();
				return;
			}
			clamped.Intersect(frameRect);
			region.Rewind();
			region.AddRect(clamped);
			return;
		}

		var scratch = _clampScratch ??= new SKPath();
		scratch.Rewind();
		scratch.AddRect(frameRect);
		region.Op(scratch, SKPathOp.Intersect, region);
	}
}
