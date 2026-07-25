// This project enables both WPF and WinForms — WinForms purely for NotifyIcon, since the tray
// icon has no WPF equivalent. That makes a handful of type names ambiguous between the two
// frameworks. This app is WPF-first, so resolve the collisions that way once, here, rather than
// fully qualifying them at every use site. WinForms types are referenced explicitly.

// Enabling WinForms drops System.IO from this project's implicit usings, so bring it back
// rather than repeating the import in every file that touches a file or a stream.
global using System.IO;

global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using Clipboard = System.Windows.Clipboard;
global using Button = System.Windows.Controls.Button;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
