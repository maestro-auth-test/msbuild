﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Globalization;
using System.Threading;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Execution
{
    /// <summary>
    /// A callback used to receive notification that a build has completed.
    /// </summary>
    /// <remarks>
    /// When this delegate is invoked, the WaitHandle on the BuildSubmission will have been be signalled and the OverallBuildResult will be valid.
    /// </remarks>
    public delegate void BuildSubmissionCompleteCallback<TRequestData, TResultData>(
        BuildSubmission<TRequestData, TResultData> submission)
        where TRequestData : BuildRequestDataBase
        where TResultData : BuildResultBase;

    public abstract class BuildSubmission<TRequestData, TResultData> : BuildSubmissionBase
        where TRequestData : BuildRequestDataBase
        where TResultData : BuildResultBase
    {
        /// <summary>
        /// The callback to invoke when the submission is complete.
        /// </summary>
        private BuildSubmissionCompleteCallback<TRequestData, TResultData>? _completionCallback;

        /// <summary>
        /// Constructor
        /// </summary>
        protected internal BuildSubmission(BuildManager buildManager, int submissionId, TRequestData requestData)
            : base(buildManager, submissionId)
        {
            ErrorUtilities.VerifyThrowArgumentNull(requestData, nameof(requestData));
            BuildRequestData = requestData;
        }

        //
        // Unfortunately covariant overrides are not available for .NET 472,
        //  so we have to use two set of properties for derived classes.
        internal override BuildResultBase? BuildResultBase => BuildResult;
        internal override BuildRequestDataBase BuildRequestDataBase => BuildRequestData;

        /// <summary>
        /// The results of the build per graph node.  Valid only after WaitHandle has become signalled.
        /// </summary>
        public TResultData? BuildResult { get; set; }

        /// <summary>
        /// The BuildRequestData being used for this submission.
        /// </summary>
        internal TRequestData BuildRequestData { get; }

        /// <summary>
        /// Starts the request and blocks until results are available.
        /// </summary>
        /// <exception cref="InvalidOperationException">The request has already been started or is already complete.</exception>
        public abstract TResultData Execute();

        /// <summary>
        /// Starts the request asynchronously and immediately returns control to the caller.
        /// </summary>
        /// <exception cref="InvalidOperationException">The request has already been started or is already complete.</exception>
        public void ExecuteAsync(BuildSubmissionCompleteCallback<TRequestData, TResultData>? callback, object? context)
        {
            ExecuteAsync(callback, context, allowMainThreadBuild: false);
        }

        protected void ExecuteAsync(
            BuildSubmissionCompleteCallback<TRequestData, TResultData>? callback,
            object? context,
            bool allowMainThreadBuild)
        {
            ErrorUtilities.VerifyThrowInvalidOperation(!IsCompleted, "SubmissionAlreadyComplete");
            _completionCallback = callback;
            AsyncContext = context;
            BuildManager.ExecuteSubmission(this, allowMainThreadBuild);
        }

        /// <summary>
        /// Sets the event signaling that the build is complete.
        /// </summary>
        internal void CompleteResults(TResultData result)
        {
            ErrorUtilities.VerifyThrowArgumentNull(result, nameof(result));
            ErrorUtilities.VerifyThrow(result.SubmissionId == SubmissionId,
                "GraphBuildResult's submission id doesn't match GraphBuildSubmission's");

            BuildResult ??= result;

            CheckForCompletion();
        }

        protected internal abstract TResultData CreateFailedResult(Exception exception);

        internal override BuildResultBase CompleteResultsWithException(Exception exception)
            => CompleteResults(exception);

        private TResultData CompleteResults(Exception exception)
        {
            TResultData result = CreateFailedResult(exception);
            CompleteResults(result);
            return result;
        }

        /// <summary>
        /// Determines if we are completely done with this submission and can complete it so the user may access results.
        /// </summary>
        protected internal override void CheckForCompletion()
        {
            if (BuildResult != null && LoggingCompleted)
            {
                bool hasCompleted = (Interlocked.Exchange(ref CompletionInvoked, 1) == 1);
                if (!hasCompleted)
                {
                    OnCompletition();

                    CompletionEvent.Set();

                    if (_completionCallback != null)
                    {
                        void Callback(object? state)
                        {
                            _completionCallback(this);
                        }

                        ThreadPoolExtensions.QueueThreadPoolWorkItemWithCulture(Callback, CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
                    }
                }
            }
        }
    }


    /// <summary>
    /// A callback used to receive notification that a build has completed.
    /// </summary>
    /// <remarks>
    /// When this delegate is invoked, the WaitHandle on the BuildSubmission will have been be signalled and the OverallBuildResult will be valid.
    /// </remarks>
    public delegate void BuildSubmissionCompleteCallback(BuildSubmission submission);

    /// <summary>
    /// A BuildSubmission represents a build request which has been submitted to the BuildManager for processing.  It may be used to
    /// execute synchronous or asynchronous build requests and provides access to the results upon completion.
    /// </summary>
    /// <remarks>
    /// This class is thread-safe.
    /// </remarks>
    public class BuildSubmission : BuildSubmission<BuildRequestData, BuildResult>
    {
        /// <summary>
        /// Flag indicating whether synchronous wait should support legacy threading semantics.
        /// </summary>
        private readonly bool _legacyThreadingSemantics;

        /// <summary>
        /// The build request for execution.
        /// </summary>
        internal BuildRequest? BuildRequest { get; set; }

        internal BuildSubmission(BuildManager buildManager, int submissionId, BuildRequestData requestData, bool legacyThreadingSemantics)
            : base(buildManager, submissionId, requestData)
        {
            _legacyThreadingSemantics = legacyThreadingSemantics;
        }

        /// <summary>
        /// Starts the request and blocks until results are available.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">The request has already been started or is already complete.</exception>
        public override BuildResult Execute()
        {
            LegacyThreadingData legacyThreadingData = ((IBuildComponentHost)BuildManager).LegacyThreadingData;
            legacyThreadingData.RegisterSubmissionForLegacyThread(SubmissionId);

            ExecuteAsync(null, null, _legacyThreadingSemantics);
            if (_legacyThreadingSemantics)
            {
                RequestBuilder.WaitWithBuilderThreadStart(new[] { WaitHandle }, false, legacyThreadingData, SubmissionId);
            }
            else
            {
                WaitHandle.WaitOne();
            }

            legacyThreadingData.UnregisterSubmissionForLegacyThread(SubmissionId);

            ErrorUtilities.VerifyThrow(BuildResult != null,
                "BuildResult is not populated after Execute is done.");

            return BuildResult!;
        }

        protected internal override BuildResult CreateFailedResult(Exception exception)
        {
            ErrorUtilities.VerifyThrow(BuildRequest != null,
                "BuildRequest is not populated while reporting failed result.");
            return new(BuildRequest!, exception);
        }
        

        protected internal override void OnCompletition()
        {
            // Did this submission have warnings elevated to errors? If so, mark it as
            // failed even though it succeeded (with warnings--but they're errors).
            if (BuildResult != null &&
                ((IBuildComponentHost)BuildManager).LoggingService.HasBuildSubmissionLoggedErrors(BuildResult.SubmissionId))
            {
                BuildResult.SetOverallResult(overallResult: false);
            }
        }
    }
}
