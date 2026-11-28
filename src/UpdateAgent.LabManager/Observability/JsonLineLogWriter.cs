using System.Text;

namespace UpdateAgent.LabManager.Observability;

/// <summary>
/// BCL만 사용하는 회전 JSONL 로그 기록기. 현재 파일 256 KiB, 회전본 5개를 넘기지 않는다.
/// 디렉터리가 쓰기 불가능하면 한 번만 경고하고 이후 이벤트는 조용히 건너뛴다.
/// </summary>
public sealed class JsonLineLogWriter
{
    public const long DefaultMaxBytes = 256 * 1024;
    public const int DefaultRetainedFiles = 5;

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _retainedFiles;
    private readonly Action<string>? _warn;
    private readonly object _gate = new();
    private bool _warned;

    public JsonLineLogWriter(
        string path,
        long maxBytes = DefaultMaxBytes,
        int retainedFiles = DefaultRetainedFiles,
        Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("로그 경로가 비어 있습니다.", nameof(path));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (retainedFiles <= 0) throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        _path = System.IO.Path.GetFullPath(path);
        _maxBytes = maxBytes;
        _retainedFiles = retainedFiles;
        _warn = warn;
    }

    public string Path => _path;

    public long MaxBytes => _maxBytes;

    public int RetainedFiles => _retainedFiles;

    /// <summary>한 줄을 원자적으로 추가하고, 용량을 넘으면 회전한 뒤 기록한다.</summary>
    public void Write(ObservabilityEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var line = @event.ToJsonLine() + "\n";
        var bytes = Encoding.UTF8.GetByteCount(line);

        lock (_gate)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                RotateIfNeeded(bytes);
                File.AppendAllText(_path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                WarnOnce();
            }
        }
    }

    private void RotateIfNeeded(long incomingBytes)
    {
        if (!File.Exists(_path)) return;
        if (new FileInfo(_path).Length + incomingBytes <= _maxBytes) return;

        for (var index = _retainedFiles; index >= 2; index--)
        {
            var target = Segment(index);
            var source = Segment(index - 1);
            if (File.Exists(target)) File.Delete(target);
            if (File.Exists(source)) File.Move(source, target);
        }

        // 현재 파일을 회전본 1로 옮겨 이전 이벤트를 보존한다. 회전본이 1개도 없으면
        // 현재 파일만 덮어쓴다.
        if (File.Exists(_path))
        {
            if (_retainedFiles > 1) File.Move(_path, Segment(1));
            else File.Delete(_path);
        }
    }

    private string Segment(int index)
    {
        var directory = System.IO.Path.GetDirectoryName(_path) ?? string.Empty;
        var name = System.IO.Path.GetFileNameWithoutExtension(_path);
        var extension = System.IO.Path.GetExtension(_path);
        return System.IO.Path.Combine(directory, $"{name}.{index}{extension}");
    }

    private void WarnOnce()
    {
        if (_warned) return;
        _warned = true;
        const string message = "관측 로그 파일에 쓸 수 없습니다. 서버 동작은 계속합니다.";
        if (_warn is not null) _warn(message);
        else Console.Error.WriteLine(message);
    }
}
