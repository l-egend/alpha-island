using System;
using System.Windows;

namespace DynamicIslandDemo
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _mainWindow;

        public SettingsWindow(MainWindow mainWindow, double currentOpacity)
        {
            InitializeComponent();
            _mainWindow = mainWindow;

            // 回显当前通透度数值
            OpacitySlider.Value = currentOpacity;
            OpacityValueText.Text = $"{(int)(currentOpacity * 100)}%";
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OpacityValueText != null)
            {
                OpacityValueText.Text = $"{(int)(e.NewValue * 100)}%";
            }

            // 实时同步通知灵动岛主窗口更新液态玻璃透明度
            _mainWindow?.UpdateGlassOpacity(e.NewValue);
        }
    }
}