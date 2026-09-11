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

namespace Hollow.Api.Consumer;

/// <summary>
/// A blob store, as a consumer sees it.
/// </summary>
/// <remarks>
/// Named <c>HollowConsumer.BlobRetriever</c> in Java; the <c>I</c> prefix follows the .NET interface
/// naming convention.
/// </remarks>
public interface IBlobRetriever
{
    /// <summary>
    /// Returns the snapshot for the greatest version at or below <paramref name="desiredVersion"/>, or
    /// <see langword="null"/> when the store holds no snapshot that old.
    /// </summary>
    Blob? RetrieveSnapshotBlob(long desiredVersion);

    /// <summary>
    /// Returns the delta that applies to <paramref name="currentVersion"/>, or <see langword="null"/>
    /// when the store holds none — which is the normal answer at the head of the chain.
    /// </summary>
    Blob? RetrieveDeltaBlob(long currentVersion);

    /// <summary>
    /// Returns the reverse delta that applies to <paramref name="currentVersion"/>, taking a consumer
    /// back one version, or <see langword="null"/> when the store holds none.
    /// </summary>
    Blob? RetrieveReverseDeltaBlob(long currentVersion);

    /// <summary>
    /// Returns the header of <paramref name="currentVersion"/>, or <see langword="null"/> when the
    /// store does not publish headers separately.
    /// </summary>
    /// <remarks>
    /// Java's default implementation throws; returning <see langword="null"/> lets a caller ask without
    /// having to know which stores support it.
    /// </remarks>
    HeaderBlob? RetrieveHeaderBlob(long currentVersion) => null;
}
