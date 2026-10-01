// Build compatibility with SkiaSharp 4.x, where the by-value SKPath.Transform and SKCanvas.SetMatrix overloads are
// obsolete-as-error: the call sites use the `in` overloads.
namespace Microsoft.UI.Composition
{
	internal static class SkiaSharpCompat
	{
		public static void TransformBy(this SkiaSharp.SKPath path, SkiaSharp.SKMatrix matrix) => path.Transform(in matrix);
		public static void TransformBy(this SkiaSharp.SKPath path, SkiaSharp.SKMatrix matrix, SkiaSharp.SKPath destination) => path.Transform(in matrix, destination);
		public static void SetMatrixBy(this SkiaSharp.SKCanvas canvas, SkiaSharp.SKMatrix matrix) => canvas.SetMatrix(in matrix);
	}
}
