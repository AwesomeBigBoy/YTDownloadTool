namespace YtDlpTool.Domain.Models;

/// <summary>The answer to 「檔案已存在」 (v1.5.0: in Domain, so ConflictDecisions can hold it).</summary>
public enum FilenameConflictResolution { Cancel, Overwrite, AutoRename }
