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

using Hollow.Core.Index;
using Hollow.Core.Memory.Pool;

namespace Hollow.Tests.Core.Index;

/// <summary>
/// The many-lists-in-one-array the hash index builder collects its matches into.
/// </summary>
/// <remarks>
/// Ported from <c>MultiLinkedElementArrayTest</c>. Lists are built by pointing each new element at the
/// one before it, so a list reads back newest first and several lists grow through each other — which
/// is what the interleaved adds below are for.
/// </remarks>
public sealed class MultiLinkedElementArrayTests
{
    [Fact]
    public void ListsGrowThroughEachOtherAndStillReadBackSeparately()
    {
        MultiLinkedElementArray array = new(WastefulRecycler.SmallArrayRecycler);

        Assert.Equal(0, array.NewList());

        array.Add(0, 100);
        array.Add(0, 200);
        array.Add(0, 300);

        Assert.Equal(1, array.NewList());

        array.Add(1, 101);

        Assert.Equal(2, array.NewList());

        array.Add(2, 102);

        Assert.Equal(3, array.NewList());

        // Back to a list that is no longer the newest, which is what makes the links matter.
        array.Add(0, 400);
        array.Add(0, 500);

        array.Add(2, 202);

        array.Add(3, 103);
        array.Add(3, 203);
        array.Add(3, 303);

        array.Add(0, 600);

        // Newest first, because each element points at the one before it.
        Assert.Equal([600, 500, 400, 300, 200, 100], array.Elements(0));
        Assert.Equal([101], array.Elements(1));
        Assert.Equal([202, 102], array.Elements(2));
        Assert.Equal([303, 203, 103], array.Elements(3));
    }

    [Fact]
    public void AZeroIsAnElementRatherThanTheEndOfAList()
    {
        // The link and the value share the storage, so a list of zeros is where an end-of-list marker
        // that was also zero would show up.
        MultiLinkedElementArray array = new(WastefulRecycler.SmallArrayRecycler);

        for (int list = 0; list < 3; list++)
        {
            Assert.Equal(list, array.NewList());

            for (int i = 0; i <= list; i++)
            {
                array.Add(list, 0);
            }
        }

        Assert.Equal([0], array.Elements(0));
        Assert.Equal([0, 0], array.Elements(1));
        Assert.Equal([0, 0, 0], array.Elements(2));
    }

    [Fact]
    public void AListSaysHowLongItIsWithoutBeingWalked()
    {
        MultiLinkedElementArray array = new(WastefulRecycler.SmallArrayRecycler);

        _ = array.NewList();
        _ = array.NewList();

        array.Add(0, 100);
        array.Add(1, 101);
        array.Add(0, 200);

        Assert.Equal(2, array.ListSize(0));
        Assert.Equal(1, array.ListSize(1));
        Assert.Equal(2, array.ListCount);
    }

    [Fact]
    public void AListThatWasNeverAddedToHasNoSize()
    {
        MultiLinkedElementArray array = new(WastefulRecycler.SmallArrayRecycler);

        _ = array.NewList();

        Assert.Equal(0, array.ListSize(0));

        // Its *elements* are deliberately not asserted. A list with nothing in it has no head to
        // start from, so walking one reads whatever is at the front of the shared element storage —
        // which belongs to some other list. Java behaves the same way and its only caller, the hash
        // index builder, never walks a list it has not added to. Size is the question to ask.
    }
}
