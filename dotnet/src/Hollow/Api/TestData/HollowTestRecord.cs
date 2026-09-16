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

using Hollow.Core;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Api.TestData;

/// <summary>
/// One record of a dataset being built by hand, for a test.
/// </summary>
/// <remarks>
/// <para>
/// A test that needs a populated dataset otherwise has to build write records and manage ordinals
/// itself, which buries what the test is about under scaffolding. These let a dataset be described
/// as a tree — a record, the records it references, and so on — and turned into a state engine at the
/// end. <see cref="Up"/> walks back to the parent, so a whole tree can be written as one expression.
/// </para>
/// <para>
/// Netflix Hollow generates typed subclasses of these from a data model with
/// <c>api.codegen.testdata</c>, which this port does not have. What is here is the runtime those
/// generated classes sit on, and it is usable by hand: subclass
/// <see cref="HollowTestObjectRecord{TParent}"/> and friends, or use
/// <see cref="HollowTestObject{TParent}"/>, which needs no subclass at all.
/// </para>
/// </remarks>
/// <typeparam name="TParent">What <see cref="Up"/> returns.</typeparam>
public abstract class HollowTestRecord<TParent>(TParent parent) : IHollowTestRecord
{
    /// <inheritdoc />
    public int Ordinal { get; private set; } = HollowConstants.OrdinalNone;

    /// <summary>The record this one was reached from.</summary>
    public TParent Up() => parent;

    /// <summary>
    /// The record at the root of the tree this one is in.
    /// </summary>
    /// <remarks>
    /// Java's <c>upTop</c> declares a type parameter that shadows the class's and casts to it
    /// unchecked, so it compiles against any expectation and fails at the call site if wrong. This
    /// casts too — the root's type is not knowable from here — but it is one explicit cast in one
    /// place, and it says what it could not prove.
    /// </remarks>
    /// <exception cref="InvalidCastException">The root is not a <typeparamref name="TRoot"/>.</exception>
    public TRoot UpTop<TRoot>()
    {
        object? root = this;

        while (root is IHollowTestRecord record && record.Parent is not null)
        {
            root = record.Parent;
        }

        return (TRoot)root!;
    }

    /// <inheritdoc />
    object? IHollowTestRecord.Parent => parent;

    /// <inheritdoc />
    public abstract HollowSchema Schema { get; }

    /// <summary>
    /// Adds this record, and everything it references, to <paramref name="writeEngine"/>.
    /// </summary>
    /// <remarks>
    /// The type state is created on first use, so a caller describing a tree never has to declare the
    /// types up front — the tree's shape is the declaration.
    /// </remarks>
    int IHollowTestRecord.AddTo(HollowWriteStateEngine writeEngine)
    {
        ArgumentNullException.ThrowIfNull(writeEngine);

        HollowSchema schema = Schema;

        if (writeEngine.GetTypeState(schema.Name) is null)
        {
            writeEngine.AddTypeState(schema switch
            {
                HollowObjectSchema objectSchema => new HollowObjectTypeWriteState(objectSchema),
                HollowListSchema listSchema => new HollowListTypeWriteState(listSchema),
                HollowSetSchema setSchema => new HollowSetTypeWriteState(setSchema),
                HollowMapSchema mapSchema => new HollowMapTypeWriteState(mapSchema),
                _ => throw new InvalidOperationException($"unknown schema kind for {schema.Name}"),
            });
        }

        Ordinal = writeEngine.Add(schema.Name, ToWriteRecord(writeEngine));

        return Ordinal;
    }

    /// <summary>Builds the write record this describes.</summary>
    protected abstract IHollowWriteRecord ToWriteRecord(HollowWriteStateEngine writeEngine);
}

/// <summary>
/// What a test record offers regardless of what its parent is.
/// </summary>
/// <remarks>
/// Java reaches across records with raw types and unchecked casts — a record holds
/// <c>HollowTestRecord&lt;?&gt;</c> children and casts on the way out. A non-generic interface says
/// the same thing without giving up on the type system: a list holds these, and nothing needs to know
/// what each one's parent was.
/// </remarks>
public interface IHollowTestRecord
{
    /// <summary>The ordinal this took, or -1 before it has been added to a state engine.</summary>
    int Ordinal { get; }

    /// <summary>The schema of the type this is a record of.</summary>
    HollowSchema Schema { get; }

    /// <summary>The record this one was reached from, or null at the root.</summary>
    internal object? Parent { get; }

    /// <summary>Adds this record and everything it references, returning its ordinal.</summary>
    internal int AddTo(HollowWriteStateEngine writeEngine);
}
