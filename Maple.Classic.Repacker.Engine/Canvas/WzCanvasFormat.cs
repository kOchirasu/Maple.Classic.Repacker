using System.ComponentModel.DataAnnotations;

namespace Maple.Classic.Repacker.Engine.Canvas;

/// <summary>
/// WZ Canvas pixel formats. Values match the raw format codes stored in <c>WzValue.Canvas.Format</c>, so conversion is a
/// plain cast. Use <c>GetDisplayName()</c> (see <c>EnumDisplayExtensions</c>) for UI labels.
/// </summary>
public enum WzCanvasFormat {
    [Display(Name = "BGRA4444 (16-bit)")]
    Bgra4444 = 1,

    [Display(Name = "BGRA8888 (32-bit)")]
    Bgra8888 = 2,

    [Display(Name = "BGR565 (16-bit)")]
    Bgr565 = 513,

    [Display(Name = "BGR565 Block16 (4-bit scale)")]
    Bgr565Block16 = 517,

    [Display(Name = "DXT3 / BC2")]
    Dxt3 = 1026,

    [Display(Name = "DXT5 / BC3")]
    Dxt5 = 2050,
}
