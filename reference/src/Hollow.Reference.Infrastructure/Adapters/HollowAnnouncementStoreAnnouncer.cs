/*
 *  Copyright 2016 Netflix, Inc.
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

using Hollow.Api.Producer;
using Hollow.Reference.Infrastructure.Storage;

namespace Hollow.Reference.Infrastructure.Adapters;

/// <summary>
/// Tells the world which version to read, once a cycle has written and validated it.
/// </summary>
/// <remarks>
/// Ported from <c>how.hollow.producer.infrastructure.DynamoDBAnnouncer</c> — and from
/// <c>S3Announcer</c>, which does the same thing to an object in the bucket. One announcer serves all
/// three modes here, because the difference between them is only where the version is written.
/// </remarks>
public sealed class HollowAnnouncementStoreAnnouncer : IAnnouncer
{
    private readonly IHollowAnnouncementStore _store;

    public HollowAnnouncementStoreAnnouncer(IHollowAnnouncementStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    public void Announce(long stateVersion, IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        Synchronously.Run(() => _store.AnnounceAsync(stateVersion, metadata));
    }
}
