// 文件: Stellaris.Engine/ImageAsset/ImageAssetTypes.cs

namespace Stellaris.Engine.ImageAsset;

/// <summary>
/// 操作状态枚举
/// </summary>
public enum OperationStatus
{
    Success,
    FileNotFound,
    UnsupportedFormat,
    InvalidParameter,
    IoError,
    OutOfMemory,
    UnknownError
}

/// <summary>
/// 导出图像格式（DDS 内部压缩格式）
/// </summary>
public enum ImageFormat
{
    Rgba8888,
    Dxt1,
    Dxt5
}

/// <summary>
/// 导出模式（决定输出文件类型）
/// </summary>
public enum ExportMode
{
    /// <summary>仅输出 DDS 文件</summary>
    DdsOnly,
    /// <summary>仅输出 PNG 文件</summary>
    PngOnly,
    /// <summary>同时输出 DDS 和 PNG 文件</summary>
    DdsAndPng
}

/// <summary>
/// 固定变换操作类型（用于TransformImage）
/// </summary>
public enum TransformOperation
{
    FlipHorizontal,
    FlipVertical,
    ScaleProportional,
    ScaleExact,
    Rotate90,
    RotateMinus90,
    Rotate180,
    Rotate270,
    RotateMinus270
}