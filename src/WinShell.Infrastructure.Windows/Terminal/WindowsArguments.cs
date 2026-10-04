// Algorithm adapted from dotnet/runtime v10.0.12 System/PasteArguments.cs.
// Copyright (c) .NET Foundation and Contributors. Licensed under the MIT license.
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
using System.Text;
namespace WinShell.Infrastructure.Windows.Terminal;

internal static class WindowsArguments
{
    public static StringBuilder Build(string executable, IReadOnlyList<string> arguments)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['\0', '"']) >= 0) throw new ArgumentException("An absolute executable path without NUL or quotes is required.", nameof(executable));
        var commandLine = new StringBuilder();
        Append(executable);
        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            if (argument.Contains('\0')) throw new ArgumentException("NUL is not permitted in arguments.", nameof(arguments));
            commandLine.Append(' ');
            Append(argument);
        }
        return commandLine;
        void Append(string value)
        {
            if (value.Length > 32766 || commandLine.Length + value.Length + 2 > 32766) throw new ArgumentException("Windows command line exceeds 32767 UTF16 units including terminator.");
            commandLine.Append('"');
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                commandLine.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                commandLine.Append(character);
                slashes = 0;
                if (commandLine.Length > 32766) throw new ArgumentException("Windows command line is too long.");
            }
            commandLine.Append('\\', slashes * 2).Append('"');
            if (commandLine.Length > 32766) throw new ArgumentException("Windows command line is too long.");
        }
    }
}
