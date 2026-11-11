using System.Windows;
using MahApps.Metro.Controls;

namespace HeavyRain;

public partial class SplashWindow : MetroWindow
{
    public SplashWindow() => InitializeComponent();
    public void ReportProgress(string message) => LoadingText.Text = message;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
