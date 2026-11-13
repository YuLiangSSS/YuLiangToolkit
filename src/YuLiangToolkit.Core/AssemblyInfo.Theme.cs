using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

// Core 里有一个需要默认样式的控件（HotkeyBox）：它的模板放在 Controls/Themes/Generic.xaml。
// ⚠ 没有这行 ThemeInfo，WPF **不会**去这个程序集里找 Generic.xaml，控件就会渲染成空白
// （踩过：改键框整个看不见，列宽被压成 0）。
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly)]

namespace YuLiangToolkit.Core;
