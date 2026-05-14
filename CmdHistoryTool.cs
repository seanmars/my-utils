#!/usr/bin/env dotnet run
#:package Spectre.Console.Cli@0.49.1

/*
<summary>
CmdHistoryTool.cs - PSReadLine 歷史紀錄管理 CLI

用途
====
管理 Windows PowerShell PSReadLine 的 ConsoleHost_history.txt, 提供兩種操作:
1. 依 prefix 批次刪除歷史紀錄行 (remove)
2. 以 whitespace 切 token 後產生分組統計樹 (find)

預設 history file 路徑:
  %APPDATA%\Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt

執行方式
========
直接以 dotnet file-based script 執行 (需要 .NET 10 SDK 以上):

  dotnet run CmdHistoryTool.cs -- <command> [options]

若已將檔案標為 executable, 也可以直接呼叫 shebang:

  ./CmdHistoryTool.cs <command> [options]

----------------------------------------------------------------------
子命令 1: remove
----------------------------------------------------------------------
刪除 history file 內 "以指定 prefix 開頭" 的行 (case-insensitive 比對).

Options:
  -s, --starts-with <PREFIX>   要比對的 prefix, 可重複指定以提供多個 prefix.
                               至少必須提供一個, 否則 validation 會失敗.
  -f, --force                  略過互動式 confirmation prompt, 直接刪除.
  -n, --dry-run                只預覽 match 結果, 不寫回 history file.
  --path <PATH>                覆寫預設 history file 路徑.

執行流程:
  1. 讀取 history file 全部 line.
  2. 用 OrdinalIgnoreCase 比對任一 prefix; 命中即列為 matched.
  3. 顯示 summary table 與 unique commands preview (最多 30 行).
  4. dry-run 時直接結束; 否則詢問確認 (除非 --force).
  5. 寫回剩餘的 line, 並建立 <history-path>.bak backup.

備份與還原:
  實際刪除前會將原檔複製為 <history-path>.bak (overwrite=true).
  還原指令:
    Copy-Item -LiteralPath '<history-path>.bak' `
              -Destination '<history-path>' -Force

注意:
  目前的 PowerShell session 仍會在記憶體中保留被刪除的 history.
  請開新 terminal, 或在當前 session 執行:
    [Microsoft.PowerShell.PSConsoleReadLine]::ClearHistory()

範例:
  # 刪除所有以 "cd " 開頭的紀錄 (有確認)
  dotnet run CmdHistoryTool.cs -- remove -s "cd "

  # 預覽所有以 "git" 或 "npm" 開頭的紀錄, 不寫檔
  dotnet run CmdHistoryTool.cs -- remove -s "git" -s "npm" --dry-run

  # 直接刪除, 不互動
  dotnet run CmdHistoryTool.cs -- remove -s "rm " --force

  # 指定自訂 history file
  dotnet run CmdHistoryTool.cs -- remove -s "ssh " --path "D:\backup\history.txt"

----------------------------------------------------------------------
子命令 2: find
----------------------------------------------------------------------
將每行 history 以 whitespace 切成 token, 建立 trie 並輸出分組統計.
可用來快速看出 "最常打的 command 開頭組合".

Options:
  -d, --depth <N>       要展開的最大樹狀深度 (預設 1, 必須 >= 1).
  -t, --top <N>         每一層只取出現次數前 N 名的子節點 (預設 10).
                        若指定 --all 則此選項被忽略.
  -a, --all             顯示每一層的所有子節點 (忽略 --top).
  -m, --min-count <N>   隱藏出現次數低於 N 的節點 (預設 1).
  --path <PATH>         覆寫預設 history file 路徑.

輸出說明:
  每一列顯示 "token1 > token2 > ..."  以及該節點累積出現次數 (count).
  count 代表 "走過該節點的 history line 數量".

範例:
  # 看最常打的前 10 個 command (depth=1)
  dotnet run CmdHistoryTool.cs -- find

  # depth=2: 顯示 command + 第一個 argument 的組合
  dotnet run CmdHistoryTool.cs -- find -d 2

  # depth=3, 列出所有出現超過 5 次的組合
  dotnet run CmdHistoryTool.cs -- find -d 3 -m 5 --all

----------------------------------------------------------------------
Exit code
----------------------------------------------------------------------
  0  成功 (含 dry-run 與使用者取消).
  1  history file 不存在或 CLI parsing 失敗.
</summary>
*/

using System.ComponentModel;
using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("CmdHistoryTool.cs");
    config.SetApplicationVersion("1.0.0");
    config.AddCommand<RemoveHistoryCommand>("remove")
        .WithDescription("Delete history lines whose start matches one or more prefixes.");
    config.AddCommand<FindCommand>("find")
        .WithDescription("Split each history line by whitespace and show grouped token tree.");
});
return app.Run(args);

public sealed class RemoveHistoryCommand : Command<RemoveHistoryCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-s|--starts-with <PREFIX>")]
        [Description("Delete every history line whose start matches this prefix (case-insensitive). Repeat the flag for multiple prefixes.")]
        public string[]? StartsWith { get; set; }

        [CommandOption("-f|--force")]
        [Description("Skip the confirmation prompt.")]
        public bool Force { get; set; }

        [CommandOption("-n|--dry-run")]
        [Description("Preview matches without modifying the history file.")]
        public bool DryRun { get; set; }

        [CommandOption("--path <PATH>")]
        [Description("Override the PSReadLine history file path.")]
        public string? Path { get; set; }

        public override ValidationResult Validate()
        {
            if (StartsWith is null || StartsWith.Length == 0)
                return ValidationResult.Error("At least one --starts-with is required.");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var historyPath = settings.Path ?? DefaultHistoryPath();
        if (!File.Exists(historyPath))
        {
            AnsiConsole.MarkupLine($"[red]History file not found:[/] {Markup.Escape(historyPath)}");
            return 1;
        }

        var lines = File.ReadAllLines(historyPath);
        if (lines.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]History file is empty.[/]");
            return 0;
        }

        var prefixes = settings.StartsWith!;
        bool Matches(string line) =>
            prefixes.Any(p => line.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        var matched = lines.Where(Matches).ToArray();
        if (matched.Length == 0)
        {
            var joined = string.Join(", ", prefixes.Select(p => $"[cyan]{Markup.Escape(p)}[/]"));
            AnsiConsole.MarkupLine($"[yellow]No lines start with:[/] {joined}");
            return 0;
        }

        var unique = matched
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var summary = new Table().Border(TableBorder.Rounded);
        summary.AddColumn("[bold]Field[/]");
        summary.AddColumn("[bold]Value[/]");
        summary.AddRow("History file", Markup.Escape(historyPath));
        summary.AddRow("Prefixes", string.Join(", ", prefixes.Select(p => $"[cyan]{Markup.Escape(p)}[/]")));
        summary.AddRow("Matched lines", $"[yellow]{matched.Length}[/]");
        summary.AddRow("Unique commands", unique.Length.ToString());
        AnsiConsole.Write(summary);

        const int previewCount = 30;
        var previewLines = unique.Take(previewCount).Select(s => $"  [grey]{Markup.Escape(s)}[/]");
        var previewBody = string.Join("\n", previewLines);
        if (unique.Length > previewCount)
            previewBody += $"\n  [grey]... and {unique.Length - previewCount} more[/]";

        AnsiConsole.Write(new Panel(previewBody)
            .Header("Preview (unique commands)")
            .Border(BoxBorder.Rounded)
            .Expand());

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine("[grey](dry-run; no changes made)[/]");
            return 0;
        }

        if (!settings.Force)
        {
            var confirm = AnsiConsole.Confirm(
                $"Delete all [red]{matched.Length}[/] line(s)?",
                defaultValue: false);
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[yellow]Aborted.[/]");
                return 0;
            }
        }

        var kept = lines.Where(l => !Matches(l)).ToArray();
        var backupPath = historyPath + ".bak";
        File.Copy(historyPath, backupPath, overwrite: true);
        File.WriteAllLines(historyPath, kept);

        AnsiConsole.MarkupLine($"[green]Removed {matched.Length} line(s).[/]");
        AnsiConsole.MarkupLine($"[grey]Backup:[/]  {Markup.Escape(backupPath)}");
        AnsiConsole.MarkupLine($"[grey]Restore:[/] [white]Copy-Item -LiteralPath '{Markup.Escape(backupPath)}' -Destination '{Markup.Escape(historyPath)}' -Force[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Note:[/] the current PowerShell session still holds these in memory.");
        AnsiConsole.MarkupLine("      Open a new terminal, or run: [bold][[Microsoft.PowerShell.PSConsoleReadLine]]::ClearHistory()[/]");
        return 0;
    }

    private static string DefaultHistoryPath() => History.DefaultPath();
}

public sealed class FindCommand : Command<FindCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-d|--depth <N>")]
        [Description("Maximum tree depth to display.")]
        [DefaultValue(1)]
        public int Depth { get; set; }

        [CommandOption("-t|--top <N>")]
        [Description("At each level, show the top N most-frequent children. Ignored when --all is set.")]
        [DefaultValue(10)]
        public int Top { get; set; }

        [CommandOption("-a|--all")]
        [Description("Show every group at every level (ignore --top).")]
        public bool All { get; set; }

        [CommandOption("-m|--min-count <N>")]
        [Description("Hide groups with fewer than N occurrences.")]
        [DefaultValue(1)]
        public int MinCount { get; set; }

        [CommandOption("--path <PATH>")]
        [Description("Override the PSReadLine history file path.")]
        public string? Path { get; set; }

        public override ValidationResult Validate()
        {
            if (Depth < 1) return ValidationResult.Error("--depth must be >= 1.");
            if (Top < 1) return ValidationResult.Error("--top must be >= 1.");
            if (MinCount < 1) return ValidationResult.Error("--min-count must be >= 1.");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var historyPath = settings.Path ?? History.DefaultPath();
        if (!File.Exists(historyPath))
        {
            AnsiConsole.MarkupLine($"[red]History file not found:[/] {Markup.Escape(historyPath)}");
            return 1;
        }

        var lines = File.ReadAllLines(historyPath);
        var root = new TrieNode();
        var considered = 0;

        foreach (var line in lines)
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            considered++;

            var node = root;
            foreach (var t in tokens)
            {
                if (!node.Children.TryGetValue(t, out var child))
                {
                    child = new TrieNode { Token = t };
                    node.Children[t] = child;
                }
                node = child;
                node.Count++;
            }
        }

        AnsiConsole.MarkupLine($"[grey]Source[/]      {Markup.Escape(historyPath)}");
        AnsiConsole.MarkupLine($"[grey]Lines used[/]  {considered}/{lines.Length}");
        AnsiConsole.MarkupLine($"[grey]Settings[/]    depth={settings.Depth}  top={settings.Top}  min-count={settings.MinCount}");
        AnsiConsole.WriteLine();

        var topChildren = root.Children.Values
            .Where(c => c.Count >= settings.MinCount)
            .OrderByDescending(c => c.Count)
            .Take(settings.All ? int.MaxValue : settings.Top)
            .ToArray();

        if (topChildren.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No groups met the threshold.[/]");
            return 0;
        }

        var rows = new List<(string Path, int Count)>();
        var path = new List<string>();
        foreach (var child in topChildren)
        {
            path.Add(child.Token);
            CollectRows(child, path, depth: 1, settings, rows);
            path.RemoveAt(path.Count - 1);
        }

        AnsiConsole.MarkupLine($"[grey]Total groups[/] {rows.Count}");
        AnsiConsole.WriteLine();

        foreach (var (rowPath, count) in rows)
        {
            AnsiConsole.MarkupLine($"[cyan]{rowPath}[/] [grey]({count})[/]");
        }
        return 0;
    }

    private static void CollectRows(TrieNode node, List<string> path, int depth, Settings settings, List<(string Path, int Count)> rows)
    {
        var children = depth >= settings.Depth
            ? Array.Empty<TrieNode>()
            : node.Children.Values
                .Where(c => c.Count >= settings.MinCount)
                .OrderByDescending(c => c.Count)
                .Take(settings.All ? int.MaxValue : settings.Top)
                .ToArray();

        if (children.Length == 0)
        {
            var joined = string.Join(" > ", path.Select(Markup.Escape));
            rows.Add((joined, node.Count));
            return;
        }

        foreach (var child in children)
        {
            path.Add(child.Token);
            CollectRows(child, path, depth + 1, settings, rows);
            path.RemoveAt(path.Count - 1);
        }
    }

    private sealed class TrieNode
    {
        public string Token { get; set; } = "";
        public int Count { get; set; }
        public Dictionary<string, TrieNode> Children { get; } = new(StringComparer.Ordinal);
    }
}

internal static class History
{
    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt");
}
