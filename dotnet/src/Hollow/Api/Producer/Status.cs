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

namespace Hollow.Api.Producer;

/// <summary>
/// Whether a producer stage succeeded.
/// </summary>
public enum StatusType
{
    /// <summary>The stage completed.</summary>
    Success,

    /// <summary>The stage threw.</summary>
    Fail,
}

/// <summary>
/// How a producer stage turned out.
/// </summary>
public sealed class Status
{
    private Status(StatusType type, Exception? cause)
    {
        Type = type;
        Cause = cause;
    }

    /// <summary>Whether the stage succeeded.</summary>
    public StatusType Type { get; }

    /// <summary>What the stage threw, or <see langword="null"/> when it succeeded.</summary>
    public Exception? Cause { get; }

    /// <summary>A successful status.</summary>
    public static Status Success { get; } = new(StatusType.Success, cause: null);

    /// <summary>
    /// A failed status carrying <paramref name="cause"/>.
    /// </summary>
    public static Status Fail(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);

        return new Status(StatusType.Fail, cause);
    }

    /// <inheritdoc />
    public override string ToString() =>
        Type == StatusType.Success ? "success" : $"fail: {Cause}";
}
