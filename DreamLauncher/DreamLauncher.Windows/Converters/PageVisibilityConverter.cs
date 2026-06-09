using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DreamLauncher.Windows.Converters;

public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is LauncherPage currentPage && parameter is string targetPageStr)
        {
            var targetPage = targetPageStr switch
            {
                "launch" => LauncherPage.Launch,
                "download" => LauncherPage.Download,
                "content" => LauncherPage.Content,
                "settings" => LauncherPage.Settings,
                _ => LauncherPage.Launch
            };
            return currentPage == targetPage ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
