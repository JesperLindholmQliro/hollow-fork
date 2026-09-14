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

namespace Hollow.Explorer.Diff;

/// <summary>
/// Where the diff UI sits in the host application's URL space.
/// </summary>
/// <remarks>
/// Every link the diff writes is built from this, so that it works the same mounted at the root of its
/// own server as mounted at <c>/diff</c> inside something larger.
/// </remarks>
public sealed class HollowDiffUIOptions
{
    private string _basePath = "";

    /// <summary>
    /// The path the diff UI is mounted at, without a trailing slash. Empty means the root.
    /// </summary>
    public string BasePath
    {
        get => _basePath;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            string trimmed = value.Trim('/');

            _basePath = trimmed.Length == 0 ? "" : "/" + trimmed;
        }
    }
}
