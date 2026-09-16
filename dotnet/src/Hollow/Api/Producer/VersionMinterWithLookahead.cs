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
/// A version minter that can be asked what the next version will be before the cycle that uses it.
/// </summary>
/// <remarks>
/// <para>
/// A producer mints a version at the start of a cycle. Something outside the producer occasionally
/// needs that number first — to name a file, to reserve a row, to tell another system what is coming —
/// and the only honest way to give it is to mint it early and hand the same one out when the cycle
/// asks. That is all this does: <see cref="Peek"/> mints and remembers, <see cref="Mint"/> returns
/// what was remembered and forgets it, so the next cycle gets a fresh one.
/// </para>
/// <para>
/// Not thread-safe, which matches how a producer uses a minter: one cycle at a time. A caller peeking
/// from another thread while a cycle is running is racing for the version either way.
/// </para>
/// </remarks>
/// <param name="mintsVersions">The minter that actually produces versions.</param>
public class VersionMinterWithLookahead(IVersionMinter mintsVersions) : IVersionMinter
{
    /// <summary>
    /// What <see cref="Peek"/> holds when it holds nothing.
    /// </summary>
    /// <remarks>
    /// Java uses -1, and so does this. It is a real version in principle — nothing in the contract
    /// forbids a minter returning it — but a version is a timestamp in every implementation there is,
    /// so the collision is theoretical. Naming it at least makes it visible.
    /// </remarks>
    private const long NothingPeeked = -1L;

    private readonly IVersionMinter _mintsVersions =
        mintsVersions ?? throw new ArgumentNullException(nameof(mintsVersions));

    private long _peeked = NothingPeeked;

    /// <summary>Whether a version has been peeked and not yet handed to a cycle.</summary>
    public bool WasPeeked => _peeked != NothingPeeked;

    /// <summary>
    /// The version the next cycle will use, minting it if it has not been minted already.
    /// </summary>
    /// <remarks>Peeking twice gives the same answer; only <see cref="Mint"/> moves it on.</remarks>
    public long Peek() => _peeked != NothingPeeked ? _peeked : _peeked = _mintsVersions.Mint();

    /// <inheritdoc />
    public long Mint()
    {
        long minted = Peek();
        _peeked = NothingPeeked;

        return minted;
    }
}
