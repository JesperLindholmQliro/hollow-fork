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

using Hollow.Sample;

// A whole Hollow deployment in one process: a producer publishing a catalogue to a directory of blobs,
// and a consumer following it and reading it back through a client nobody wrote by hand.
//
// Pass a directory to keep the blobs; without one they go to a temporary directory and are cleaned up.

// Anything beginning with a dash reached us from `dotnet run` rather than from the user.
string? keep = args.FirstOrDefault(argument => !argument.StartsWith('-'));
string blobDirectory = keep ?? Path.Combine(Path.GetTempPath(), $"hollow-sample-{Environment.ProcessId}");

Directory.CreateDirectory(blobDirectory);

try
{
    Consuming.ReadTheCatalogue(blobDirectory, Producing.PublishTheCatalogue(blobDirectory));

    Console.WriteLine();
    Console.WriteLine(
        keep is null
            ? "Done. Run again with a directory argument to keep the blobs."
            : $"Done. The blobs are in {blobDirectory}.");
}
finally
{
    if (keep is null)
    {
        Directory.Delete(blobDirectory, recursive: true);
    }
}
