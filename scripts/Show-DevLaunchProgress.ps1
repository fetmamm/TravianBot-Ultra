param(
    [Parameter(Mandatory = $true)]
    [string]$StatusPath,
    [string]$ReadyPath,
    [string]$CancelPath,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationFramework

# Match the Desktop palette without loading the app that is being built.
$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="430" Height="162" WindowStartupLocation="CenterScreen"
        WindowStyle="None" ResizeMode="NoResize" AllowsTransparency="True"
        Background="Transparent" ShowInTaskbar="True" Topmost="True"
        FontFamily="Segoe UI">
    <Window.Resources>
        <Style TargetType="Button">
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="ButtonBorder" Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1"
                                CornerRadius="4">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="ButtonBorder" Property="BorderBrush" Value="#6E7681" />
                            </Trigger>
                            <Trigger Property="IsKeyboardFocused" Value="True">
                                <Setter TargetName="ButtonBorder" Property="BorderBrush" Value="#58A6FF" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </Window.Resources>
    <Border Margin="6" Padding="22,17" CornerRadius="10"
            Background="#161B22" BorderBrush="#30363D" BorderThickness="1">
        <Border.Effect>
            <DropShadowEffect BlurRadius="17" ShadowDepth="4" Opacity="0.35" Color="#010409" />
        </Border.Effect>
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="16" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="10" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="14" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="12" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <Grid x:Name="DragHandle" Grid.Row="0" Background="Transparent">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBlock Text="Starting Tbot Ultra" Foreground="#E6EDF3"
                           FontSize="17" FontWeight="SemiBold" />
                <TextBlock x:Name="StepText" Grid.Column="1" Text="STARTING"
                           Foreground="#3FB950" FontSize="10" FontWeight="SemiBold"
                           VerticalAlignment="Center" />
                <Button x:Name="MinimizeButton" Grid.Column="2"
                        Width="28" Height="24" Margin="12,-2,0,0"
                        Background="#21262D" Foreground="#ADBAC7"
                        BorderBrush="#30363D" BorderThickness="1"
                        ToolTip="Minimize">
                    <Rectangle Width="10" Height="1" Fill="#ADBAC7" />
                </Button>
                <Button x:Name="CloseButton" Grid.Column="3"
                        Width="28" Height="24" Margin="6,-2,0,0"
                        Background="#2D1213" Foreground="#FF7B72"
                        BorderBrush="#B62324" BorderThickness="1"
                        ToolTip="Cancel startup">
                    <Path Data="M 2,2 L 10,10 M 10,2 L 2,10" Width="12" Height="12"
                          Stroke="#FF7B72" StrokeThickness="1.4"
                          StrokeStartLineCap="Round" StrokeEndLineCap="Round" />
                </Button>
            </Grid>
            <ProgressBar x:Name="LaunchProgress" Grid.Row="2" Height="9"
                         Minimum="0" Maximum="100" Value="4" Foreground="#3FB950">
                <ProgressBar.Template>
                    <ControlTemplate TargetType="ProgressBar">
                        <Grid x:Name="PART_Track" Background="#21262D">
                            <Border x:Name="PART_Indicator" Background="{TemplateBinding Foreground}"
                                    HorizontalAlignment="Left" />
                        </Grid>
                    </ControlTemplate>
                </ProgressBar.Template>
            </ProgressBar>
            <Grid Grid.Row="4">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBlock x:Name="StatusText" Text="Checking files..."
                           Foreground="#ADBAC7" FontSize="12" TextTrimming="CharacterEllipsis" />
                <TextBlock x:Name="ElapsedText" Grid.Column="1" Margin="12,0,0,0"
                           Text="Elapsed: 00:00:00" Foreground="#909DAB"
                           FontFamily="Consolas" FontSize="11" VerticalAlignment="Center" />
            </Grid>
            <Border x:Name="ErrorPanel" Grid.Row="6" Visibility="Collapsed"
                    Background="#2D1213" BorderBrush="#F85149" BorderThickness="1"
                    CornerRadius="6" Padding="12">
                <Grid>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="70" />
                        <RowDefinition Height="8" />
                        <RowDefinition Height="Auto" />
                    </Grid.RowDefinitions>
                    <TextBox x:Name="ErrorDetails" Grid.Row="0" IsReadOnly="True"
                             Background="Transparent" BorderThickness="0" Foreground="#E6EDF3"
                             FontFamily="Consolas" FontSize="11" TextWrapping="Wrap"
                             VerticalScrollBarVisibility="Auto" />
                    <TextBlock x:Name="LogPathText" Grid.Row="2" Foreground="#ADBAC7"
                               FontSize="10" TextTrimming="CharacterEllipsis" />
                </Grid>
            </Border>
            <Button x:Name="OpenLogButton" Grid.Row="8" Content="Open log"
                    Width="92" Height="29" HorizontalAlignment="Right" Visibility="Collapsed"
                    Background="#21262D" Foreground="#E6EDF3"
                    BorderBrush="#30363D" BorderThickness="1" />
        </Grid>
    </Border>
</Window>
'@

$reader = [System.Xml.XmlReader]::Create([System.IO.StringReader]::new($xaml))
try {
    $window = [Windows.Markup.XamlReader]::Load($reader)
}
finally {
    $reader.Dispose()
}

$progress = $window.FindName("LaunchProgress")
$statusText = $window.FindName("StatusText")
$stepText = $window.FindName("StepText")
$elapsedText = $window.FindName("ElapsedText")
$errorPanel = $window.FindName("ErrorPanel")
$errorDetails = $window.FindName("ErrorDetails")
$logPathText = $window.FindName("LogPathText")
$openLogButton = $window.FindName("OpenLogButton")
$closeButton = $window.FindName("CloseButton")
$minimizeButton = $window.FindName("MinimizeButton")
$dragHandle = $window.FindName("DragHandle")
$script:logPath = $null
$closeButton.Add_Click({ $window.Close() })
$minimizeButton.Add_Click({ $window.WindowState = [Windows.WindowState]::Minimized })
$dragHandle.Add_MouseLeftButtonDown({
    if ($_.LeftButton -eq [Windows.Input.MouseButtonState]::Pressed -and
        -not $minimizeButton.IsMouseOver -and -not $closeButton.IsMouseOver) {
        $window.DragMove()
    }
})
$openLogButton.Add_Click({
    if ($script:logPath -and [System.IO.File]::Exists($script:logPath)) {
        try {
            $window.Topmost = $false
            Start-Process -FilePath "notepad.exe" -ArgumentList ('"' + $script:logPath + '"')
        }
        catch {
            $statusText.Text = "Could not open the log. Its path is shown below."
        }
    }
})
$script:launchTimer = [System.Diagnostics.Stopwatch]::StartNew()
$closeTimer = [Windows.Threading.DispatcherTimer]::new()
$closeTimer.Interval = [TimeSpan]::FromMilliseconds(650)
$closeTimer.Add_Tick({
    $closeTimer.Stop()
    $window.Close()
})

$timer = [Windows.Threading.DispatcherTimer]::new()
$timer.Interval = [TimeSpan]::FromMilliseconds(150)
$timer.Add_Tick({
    try {
        $lines = [System.IO.File]::ReadAllLines($StatusPath)
        if ($lines.Length -eq 0) { return }
        $parts = $lines[0].Trim().Split('|', 2)
        if ($parts.Length -ne 2) { return }
        $value = 0
        if (-not [int]::TryParse($parts[0], [ref]$value)) { return }

        $elapsed = $script:launchTimer.Elapsed
        $elapsedText.Text = "Elapsed: {0:00}:{1:00}:{2:00}" -f `
            [Math]::Floor($elapsed.TotalHours), $elapsed.Minutes, $elapsed.Seconds
        $statusText.Text = $parts[1]
        if ($value -lt 0) {
            if ($lines.Length -lt 3) { return }
            $window.Width = 520
            $window.Height = 320
            $errorPanel.Visibility = [Windows.Visibility]::Visible
            $openLogButton.Visibility = [Windows.Visibility]::Visible
            $errorDetails.Text = $lines[1]
            $script:logPath = if ($lines[2] -eq "-") { $null } else { $lines[2] }
            $logPathText.Text = if ($script:logPath) { "Log: $script:logPath" } else { "No log was written." }
            $openLogButton.IsEnabled = [bool]($script:logPath -and [System.IO.File]::Exists($script:logPath))
            $statusText.Foreground = [Windows.Media.Brushes]::IndianRed
            $stepText.Text = "FAILED"
            $stepText.Foreground = [Windows.Media.Brushes]::IndianRed
            $progress.Foreground = [Windows.Media.Brushes]::IndianRed
            $timer.Stop()
        }
        else {
            $progress.Value = [Math]::Min(100, $value)
            if ($value -ge 100) {
                $stepText.Text = "READY"
                $timer.Stop()
                $closeTimer.Start()
            }
        }
    }
    catch [System.IO.IOException] {
        # The VBS launcher may be replacing the short status file during this poll.
    }
    catch [System.UnauthorizedAccessException] {
        # Keep the window responsive if an antivirus briefly holds the status file.
    }
})

$window.Add_Closing({
    if ($CancelPath -and $stepText.Text -ne "READY" -and $stepText.Text -ne "FAILED") {
        $closingEvent = $_
        try {
            [System.IO.File]::WriteAllText($CancelPath, "cancel")
        }
        catch {
            $closingEvent.Cancel = $true
            $statusText.Text = "Could not cancel launch. Try Close again."
        }
    }
})
$window.Add_Closed({
    $timer.Stop()
    $closeTimer.Stop()
    Remove-Item -LiteralPath $StatusPath -ErrorAction SilentlyContinue
    if ($ReadyPath) {
        Remove-Item -LiteralPath $ReadyPath -ErrorAction SilentlyContinue
    }
})
if ($ReadyPath) {
    $window.Add_ContentRendered({
        [System.IO.File]::WriteAllText($ReadyPath, "ready")
    })
}
if ($ValidateOnly) {
    return
}
$timer.Start()
$window.ShowDialog() | Out-Null
