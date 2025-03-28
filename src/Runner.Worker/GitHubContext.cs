﻿using GitHub.DistributedTask.Pipelines.ContextData;
using System;
using System.Collections.Generic;

namespace GitHub.Runner.Worker
{
    public sealed class GitHubContext : DictionaryContextData, IEnvironmentContextData
    {
        public GitHubContext(string[] prefixes = null) {
            Prefixes = prefixes ?? new [] { "github" };
        }

        private readonly HashSet<string> _contextEnvAllowlist = new(StringComparer.OrdinalIgnoreCase)
        {
            "action_path",
            "action_ref",
            "action_repository",
            "action",
            "actor",
            "actor_id",
            "api_url",
            "base_ref",
            "env",
            "event_name",
            "event_path",
            "graphql_url",
            "head_ref",
            "job",
            "output",
            "path",
            "ref_name",
            "ref_protected",
            "ref_type",
            "ref",
            "repository",
            "repository_id",
            "repository_owner",
            "repository_owner_id",
            "retention_days",
            "run_attempt",
            "run_id",
            "run_number",
            "server_url",
            "sha",
            "state",
            "step_summary",
            "triggering_actor",
            "workflow",
            "workflow_ref",
            "workflow_sha",
            "workspace"
        };

        public string[] Prefixes { get; }

        public IEnumerable<KeyValuePair<string, string>> GetRuntimeEnvironmentVariables()
        {
            foreach (var data in this)
            {
                if (_contextEnvAllowlist.Contains(data.Key))
                {
                    foreach (var prefix in Prefixes)
                    {
                        if (data.Value is StringContextData value)
                        {
                            yield return new KeyValuePair<string, string>($"{prefix.ToUpperInvariant()}_{data.Key.ToUpperInvariant()}", value);
                        }
                        else if (data.Value is BooleanContextData booleanValue)
                        {
                            yield return new KeyValuePair<string, string>($"{prefix.ToUpperInvariant()}_{data.Key.ToUpperInvariant()}", booleanValue.ToString());
                        }
                    }
                }
            }
        }

        public GitHubContext ShallowCopy()
        {
            var copy = new GitHubContext(Prefixes);

            foreach (var pair in this)
            {
                copy[pair.Key] = pair.Value;
            }

            return copy;
        }
    }
}
