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

namespace Hollow.Api.Producer;

/// <summary>
/// Mints versions as a UTC timestamp with a three-digit counter, giving
/// <c>yyyyMMddHHmmssNNN</c>.
/// </summary>
/// <remarks>
/// Readable at a glance and ascending, which is all a version has to be. The counter distinguishes
/// cycles within the same second; a producer running more than a thousand cycles in one second would
/// repeat a version, which is not a rate any real producer reaches.
/// </remarks>
public sealed class VersionMinterWithCounter : IVersionMinter
{
    private static int _counter;

    /// <inheritdoc />
    public long Mint()
    {
        string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        int sequence = Interlocked.Increment(ref _counter) % 1000;

        return long.Parse(
            $"{timestamp}{sequence.ToString("D3", CultureInfo.InvariantCulture)}",
            CultureInfo.InvariantCulture);
    }
}
