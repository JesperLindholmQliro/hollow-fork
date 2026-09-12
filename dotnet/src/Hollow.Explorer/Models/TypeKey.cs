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

namespace Hollow.Explorer.Models;

/// <summary>
/// One entry of the list of records down the side of the browse page.
/// </summary>
/// <param name="Index">Its position in the type, counting from the first record rather than the page.</param>
/// <param name="Ordinal">The ordinal it lives at.</param>
/// <param name="Key">The key as a link should carry it, with any delimiter inside a value escaped.</param>
/// <param name="KeyDisplay">
/// The key as it should be read, unescaped — or <c>ORDINAL:n</c> for a type with no key, which has
/// nothing else to be called by.
/// </param>
public sealed record TypeKey(int Index, int Ordinal, string Key, string KeyDisplay);
