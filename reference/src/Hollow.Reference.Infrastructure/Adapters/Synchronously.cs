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

using System.Runtime.ExceptionServices;

namespace Hollow.Reference.Infrastructure.Adapters;

/// <summary>
/// The one place asynchronous work is waited on synchronously, and the reason it has to be.
/// </summary>
/// <remarks>
/// <para>
/// Hollow's <c>IPublisher</c>, <c>IAnnouncer</c> and <c>IBlobRetriever</c> are synchronous, and both
/// cloud SDKs are asynchronous only. Something has to bridge the two, and doing it here — once, named,
/// with the reason written down — is better than sprinkling <c>.Result</c> through four adapters.
/// </para>
/// <para>
/// Blocking a thread this way deadlocks where a synchronization context resumes continuations on the
/// thread being blocked. Neither host this runs in has one: a console application and an ASP.NET Core
/// request both continue on the thread pool. The producer's cycle is meant to occupy its thread
/// anyway, and the consumer's refresh already runs off the request path.
/// </para>
/// <para>
/// <see cref="Task.GetAwaiter"/> rather than <see cref="Task{T}.Result"/> so that a failure arrives as
/// what was thrown rather than wrapped in an <see cref="AggregateException"/>, and the stack trace it
/// carries is the one from inside the operation.
/// </para>
/// </remarks>
internal static class Synchronously
{
    internal static void Run(Func<Task> operation)
    {
        try
        {
            operation().GetAwaiter().GetResult();
        }
        catch (AggregateException aggregate) when (aggregate.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(aggregate.InnerExceptions[0]).Throw();
        }
    }

    internal static T Run<T>(Func<Task<T>> operation)
    {
        try
        {
            return operation().GetAwaiter().GetResult();
        }
        catch (AggregateException aggregate) when (aggregate.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(aggregate.InnerExceptions[0]).Throw();
            throw;
        }
    }
}
