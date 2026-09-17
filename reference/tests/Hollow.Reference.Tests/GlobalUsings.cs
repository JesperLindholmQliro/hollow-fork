// xUnit is used by every file here, so it is imported once rather than at the top of each of them.
global using Xunit;

// Token, which is the running test's cancellation token. Every asynchronous call is given one, so that
// a test hanging is a cancelled test rather than a stuck run.
global using static Hollow.Reference.Tests.Cancellation;
