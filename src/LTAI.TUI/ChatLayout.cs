using System.Text;
using LTAI.Agent.Agents;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class ChatLayout
{
    private readonly ChatAgent _chat;
    private readonly List<(string role, string content)> _history = new();
    private readonly StringBuilder _responseBuffer = new();

    public ChatLayout(ChatAgent chat) => _chat = chat;

    public async Task RenderAsync()
    {
        AnsiConsole.MarkupLine("[bold]Chat — type your message, empty line to return[/]");

        while (true)
        {
            var input = AnsiConsole.Ask<string>("[grey]>[/]");
            if (string.IsNullOrEmpty(input)) return;

            _history.Add(("user", input));
            AnsiConsole.MarkupLine("[yellow]Thinking...[/]");

            try
            {
                var response = await _chat.ChatAsync(input);
                _history.Add(("assistant", response));
                AnsiConsole.MarkupLine($"[green]{response.EscapeMarkup()}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
            }
        }
    }
}
