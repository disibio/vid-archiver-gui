using VidArchiverGui.Core.Services;

namespace VidArchiverGui.Core.Tests;

public class ArgumentParserTests
{
    [Fact]
    public void Splits_users_command_with_quoted_windows_path()
    {
        var args = ArgumentParser.Split(
            """-f bestvideo[height<=?1480]+bestaudio/best --write-comments  --download-archive "E:\ARCHIVE\YT\archive.txt" --merge-output-format mkv   --mtime""");

        Assert.Equal(
            ["-f", "bestvideo[height<=?1480]+bestaudio/best", "--write-comments", "--download-archive", @"E:\ARCHIVE\YT\archive.txt", "--merge-output-format", "mkv", "--mtime"],
            args);
    }

    [Fact]
    public void Handles_newlines_comments_and_leading_executable()
    {
        var args = ArgumentParser.Split("yt-dlp.exe -x\n# a comment --ignored\n  --audio-format 'mp3'\r\n");
        Assert.Equal(["-x", "--audio-format", "mp3"], args);
    }

    [Fact]
    public void Unterminated_quote_does_not_gain_a_trailing_newline()
    {
        Assert.Equal(["-o", "abc"], ArgumentParser.Split("-o \"abc"));
        Assert.Equal(["-o", "a\nb"], ArgumentParser.Split("-o \"a\nb"));
    }

    [Fact]
    public void Keeps_empty_quoted_argument()
    {
        Assert.Equal(["--sub-lang", ""], ArgumentParser.Split("--sub-lang \"\""));
    }

    [Fact]
    public void Extracts_only_access_options_for_metadata_lookup()
    {
        var args = ArgumentParser.Split("--write-comments --cookies-from-browser firefox -f best --proxy=socks5://x:1 --netrc --embed-subs");
        Assert.Equal(["--cookies-from-browser", "firefox", "--proxy=socks5://x:1", "--netrc"], ArgumentParser.ExtractAccessOptions(args));
    }
}
