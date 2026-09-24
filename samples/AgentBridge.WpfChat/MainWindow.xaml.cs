using System.Windows;
using AgentBridge.Core;
using AgentBridge.Wpf;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentBridge.WpfChat;

public partial class MainWindow : Window
{
    private AgentRunController? controller;

    public MainWindow()
    {
        this.InitializeComponent();
        this.Loaded += this.OnLoaded;
        this.Closed += this.OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        IChatClient? chatClient = OpenAiChatClientFactory.TryCreate(out string? error);
        if (chatClient is null)
        {
            this.SendButton.IsEnabled = false;
            this.StatusText.Text = error;
            this.AppendLine(error ?? "チャット クライアントを作成できませんでした。");
            return;
        }

        DispatcherMarshaller marshaller = new(this.Dispatcher);
        AIFunction getTime = AIFunctionFactory.Create(
            static () => DateTimeOffset.Now.ToString("O"),
            name: "get_local_time",
            description: "このマシンのローカル時刻を ISO 8601 で返す");
        AIFunction setStatus = AIFunctionFactory.Create(
            (string message) =>
            {
                this.StatusText.Text = message;
                return "status updated";
            },
            name: "set_status",
            description: "ウィンドウ下部のステータス文言を更新する");

        ChatClientAgent agent = AgentBridgeHost.Create(
            chatClient,
            [getTime, setStatus],
            marshaller,
            new AgentBridgeHostOptions
            {
                Name = "sample-chat",
                Instructions = "短い日本語で答える。必要ならツールを使う。",
            });
        AgentSession session = await agent.CreateSessionAsync();
        this.controller = new AgentRunController(agent, session);
        this.controller.PropertyChanged += this.OnControllerPropertyChanged;
        this.StatusText.Text = $"model={OpenAiChatClientFactory.ResolveModel()}";
        this.AppendLine("準備できました。メッセージを送ってください。Stop で中断できます。");
        this.UpdateBusy();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (this.controller is { } current)
        {
            current.PropertyChanged -= this.OnControllerPropertyChanged;
            current.Dispose();
        }
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        if (this.controller is null || this.controller.IsBusy)
        {
            return;
        }

        string text = this.InputBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        this.InputBox.Clear();
        this.AppendLine("You: " + text);
        this.Transcript.AppendText("Assistant: ");
        try
        {
            await foreach (AgentResponseUpdate update in this.controller.RunStreamingAsync(text))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    this.Transcript.AppendText(update.Text);
                    this.Transcript.ScrollToEnd();
                    continue;
                }

                foreach (AIContent content in update.Contents)
                {
                    if (content is FunctionCallContent call)
                    {
                        this.AppendLine(string.Empty);
                        this.AppendLine($"tool: {call.Name}");
                    }
                }
            }

            this.AppendLine(string.Empty);
        }
        catch (OperationCanceledException)
        {
            this.AppendLine(string.Empty);
            this.AppendLine("(stopped)");
        }
        catch (Exception exception)
        {
            this.AppendLine(string.Empty);
            this.AppendLine("error: " + exception.Message);
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        this.controller?.Cancel();
    }

    private void OnControllerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentRunController.IsBusy))
        {
            this.UpdateBusy();
        }
    }

    private void UpdateBusy()
    {
        bool busy = this.controller?.IsBusy == true;
        this.SendButton.IsEnabled = this.controller is not null && !busy;
        this.StopButton.IsEnabled = busy;
        this.InputBox.IsEnabled = this.controller is not null && !busy;
    }

    private void AppendLine(string line)
    {
        this.Transcript.AppendText(line + Environment.NewLine);
        this.Transcript.ScrollToEnd();
    }
}
