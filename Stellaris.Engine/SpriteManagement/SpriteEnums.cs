// 文件: Stellaris.Engine/SpriteManagement/SpriteEnums.cs

namespace Stellaris.Engine.SpriteManagement;

/// <summary>
/// 子图形管理引擎操作状态枚举
/// </summary>
public enum SpriteOperationStatus
{
    /// <summary>操作成功完成</summary>
    Success,

    /// <summary>指定的 .gfx 文件不存在</summary>
    FileNotFound,

    /// <summary>查询或删除时指定的名称不存在</summary>
    SpriteNotFound,

    /// <summary>添加时名称已存在且模式为 Error</summary>
    SpriteAlreadyExists,

    /// <summary>参数无效（如路径为空、texturefile 非 .dds）</summary>
    InvalidParameter,

    /// <summary>文件读写错误</summary>
    IoError,

    /// <summary>.gfx 文件解析失败</summary>
    ParseError,

    /// <summary>加载 .dds 文件失败</summary>
    ImageLoadError,

    /// <summary>内存不足</summary>
    OutOfMemory,

    /// <summary>其他未分类错误</summary>
    UnknownError
}

/// <summary>
/// 操作模式（添加或更新时同名处理方式）
/// </summary>
public enum OperationMode
{
    /// <summary>若同名已存在，覆盖之</summary>
    Overwrite,

    /// <summary>若同名已存在，跳过本次操作（返回成功）</summary>
    Skip,

    /// <summary>若同名已存在，抛出异常或返回失败状态</summary>
    Error
}