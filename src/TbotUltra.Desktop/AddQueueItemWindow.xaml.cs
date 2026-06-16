using System.Text.Json.Nodes;
using System.Windows;

namespace TbotUltra.Desktop;

public partial class AddQueueItemWindow : Window
{
    public string TaskName { get; private set; } = string.Empty;
    public int Priority { get; private set; }
    public int MaxRetries { get; private set; } = 3;
    public Dictionary<string, string> Payload { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public AddQueueItemWindow(IEnumerable<string> allowedTasks)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        TaskComboBox.ItemsSource = allowedTasks.ToList();
        TaskComboBox.SelectedIndex = 0;
    }

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selectedTask = TaskComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selectedTask))
            {
                throw new InvalidOperationException("Task is required.");
            }

            if (!int.TryParse(PriorityTextBox.Text.Trim(), out var priority))
            {
                throw new InvalidOperationException("Priority must be an integer.");
            }

            if (!int.TryParse(MaxRetriesTextBox.Text.Trim(), out var maxRetries) || maxRetries < 0)
            {
                throw new InvalidOperationException("Max retries must be an integer >= 0.");
            }

            var payloadJson = PayloadTextBox.Text.Trim();
            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (payloadJson.Length > 0 && payloadJson != "{}")
            {
                var parsed = JsonNode.Parse(payloadJson) as JsonObject
                    ?? throw new InvalidOperationException("Payload must be a JSON object.");

                foreach (var pair in parsed)
                {
                    if (pair.Value is null)
                    {
                        continue;
                    }

                    payload[pair.Key] = pair.Value.ToString();
                }
            }

            TaskName = selectedTask.Trim();
            Priority = priority;
            MaxRetries = maxRetries;
            Payload = payload;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, "Add queue item", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

