namespace ScrcpyX;

public enum FileIconKind
{
    Folder,
    Image,
    Video,
    Audio,
    Archive,
    Apk,
    Text,
    File
}

public static class FileKind
{
    public static FileIconKind Classify(string? name, bool isDirectory)
    {
        if (isDirectory)
            return FileIconKind.Folder;
        return Extension(name) switch
        {
            "jpg" or "jpeg" or "png" or "gif" or "webp" or "heic" or "bmp" => FileIconKind.Image,
            "mp4" or "mkv" or "mov" or "avi" or "webm" => FileIconKind.Video,
            "mp3" or "m4a" or "wav" or "flac" or "ogg" => FileIconKind.Audio,
            "zip" or "rar" or "7z" or "tar" or "gz" => FileIconKind.Archive,
            "apk" => FileIconKind.Apk,
            "txt" or "md" or "log" or "json" or "xml" or "csv" => FileIconKind.Text,
            _ => FileIconKind.File
        };
    }

    public static string Glyph(FileIconKind kind) => kind switch
    {
        FileIconKind.Folder => "\uED25",
        FileIconKind.Image => "\uE8B9",
        FileIconKind.Video => "\uE714",
        FileIconKind.Audio => "\uE8D6",
        FileIconKind.Archive => "\uE7B8",
        FileIconKind.Apk => "\uE74C",
        FileIconKind.Text => "\uE9F9",
        _ => "\uE8A5"
    };

    private static string Extension(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "";
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
            return "";
        return name[(dot + 1)..].ToLowerInvariant();
    }
}
