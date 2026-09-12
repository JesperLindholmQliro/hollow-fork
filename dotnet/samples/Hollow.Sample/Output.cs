/*
 *  Copyright 2016-2019 Netflix, Inc.
 *
 *     Licensed under the Apache License, Version 2.0 (the "License");
 *     you may not use this file except in compliance with the License.
 *     You may obtain a copy of the License at
 *
 *         http://www.apache.org/licenses/LICENSE-2.0
 *
 *     Unless required by applicable law or agreed to in writing, software
 *     distributed under the License is distributed on an "AS IS" BASIS,
 *     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *     See the License for the specific language governing permissions and
 *     limitations under the License.
 *
 */

using System.Globalization;

namespace Hollow.Sample;

// Console formatting, kept out of the way so the Hollow code reads as Hollow code.
internal static class Output
{
    private static int _act;

    internal static void Act(string title)
    {
        _act++;

        Console.WriteLine();
        Console.WriteLine($"══ {_act}. {title} ".PadRight(78, '═'));
    }

    internal static void Step(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title}");
    }

    internal static void Say(string line) => Console.WriteLine($"   {line}");

    internal static void Item(string line) => Console.WriteLine($"     · {line}");

    internal static void Note(string line) => Console.WriteLine($"   ({line})");

    /// <summary>
    /// Formats money the way the rest of the port formats anything numeric: with the culture named
    /// rather than taken from whatever the machine happens to be set to. A budget read out of a blob
    /// has to say the same thing wherever it is read, which is also why it is stored as a decimal.
    /// </summary>
    internal static string Money(decimal? amount) =>
        amount is { } value ? $"${value.ToString("N0", CultureInfo.InvariantCulture)}" : "unknown";

    /// <summary>A number, with the culture named for the same reason.</summary>
    internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
