using System.Windows;
using System.Windows.Media.Animation;
using HeavyRain.Core;
using MahApps.Metro.Controls;

namespace HeavyRain;

public partial class ActionNoticeWindow : MetroWindow
{
    public ActionNoticeWindow(ActionBlocker blocker)
    {
        InitializeComponent();
        NoticeTitle.Text = blocker.Title;
        NoticeMessage.Text = blocker.Message;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        DismissButton.Focus();
        if (!SystemParameters.ClientAreaAnimation) return;
        var origin = Left;
        if (!double.IsFinite(origin)) return;
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360), FillBehavior = FillBehavior.Stop };
        int[] offsets = [0, -10, 10, -8, 8, -5, 5, -2, 0];
        for (int index = 0; index < offsets.Length; index++)
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(origin + offsets[index], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(index * 45))));
        BeginAnimation(LeftProperty, shake);
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Close();
}
