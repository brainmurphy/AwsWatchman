﻿using System;
using System.Threading.Tasks;
using Watchman.AwsResources;
using Watchman.AwsResources.Services.Sqs;
using Watchman.Configuration;
using Watchman.Engine.Logging;
using Watchman.Engine.Sns;

namespace Watchman.Engine.Generation.Sqs
{
    public class SqsAlarmGenerator
    {
        private readonly IAlarmLogger _logger;
        private readonly IResourceSource<QueueData> _queueSource;
        private readonly QueueNamePopulator _queueNamePopulator;
        private readonly IQueueAlarmCreator _queueAlarmCreator;
        private readonly SnsCreator _snsCreator;

        private static readonly ErrorQueue ErrorQueueDefaults = new ErrorQueue
            {
                Monitored = true,
                Suffix = "_error",
                LengthThreshold = AwsConstants.ErrorQueueLengthThreshold,
                OldestMessageThreshold = null
            };

        public SqsAlarmGenerator(IAlarmLogger logger,
            IResourceSource<QueueData> queueSource,
            QueueNamePopulator queueNamePopulator,
            IQueueAlarmCreator queueAlarmCreator,
            SnsCreator snsCreator)
        {
            _logger = logger;
            _queueSource = queueSource;
            _queueNamePopulator = queueNamePopulator;
            _queueAlarmCreator = queueAlarmCreator;
            _snsCreator = snsCreator;
        }

        public async Task GenerateAlarmsFor(WatchmanConfiguration config, RunMode mode)
        {
            var dryRun = mode == RunMode.DryRun;

            await LogQueueNames();

            foreach (var alertingGroup in config.AlertingGroups)
            {
                await GenerateAlarmsFor(alertingGroup, dryRun);
            }

            ReportPutCounts(dryRun);
        }

        private async Task LogQueueNames()
        {
            var queueNames = await _queueSource.GetResourceNamesAsync();
            if (queueNames == null)
            {
                _logger.Info("No queues found");
                return;
            }

            _logger.Info($"Preloaded all {queueNames.Count} queues");

            foreach (var queueName in queueNames)
            {
                _logger.Detail(queueName);
            }
        }

        private async Task GenerateAlarmsFor(AlertingGroup alertingGroup, bool dryRun)
        {
            if (alertingGroup.Sqs?.Queues == null || alertingGroup.Sqs.Queues.Count == 0)
            {
                return;
            }

            if (alertingGroup.Sqs.Errors == null)
            {
                alertingGroup.Sqs.Errors = new ErrorQueue();
            }
            alertingGroup.Sqs.Errors.ReadDefaults(ErrorQueueDefaults);

            await _queueNamePopulator.PopulateSqsNames(alertingGroup);

            var snsTopic = await _snsCreator.EnsureSnsTopic(alertingGroup, dryRun);

            foreach (var queue in alertingGroup.Sqs.Queues)
            {
                await EnsureQueueAlarms(alertingGroup, queue, snsTopic, dryRun);
            }
        }

        private async Task EnsureQueueAlarms(
            AlertingGroup group, Queue queue,
            string snsTopic, bool dryRun)
        {
            try
            {
                var queues = await _queueSource.GetResourceNamesAsync();

                if (queue.Errors == null)
                {
                    queue.Errors = new ErrorQueue();
                }
                queue.Errors.ReadDefaults(group.Sqs.Errors);

                if (! queues.Contains(queue.Name))
                {
                    _logger.Info($"No match in active queues for queue {queue.Name}");
                    return;
                }

                if (!queue.Errors.Monitored.Value && IsErrorQueue(queue))
                {
                    _logger.Info($"Skipping error queue {queue.Name}");
                    return;
                }

                await EnsureActiveQueueAlarms(group, queue, snsTopic, dryRun);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error when creating queue alarm for {queue.Name}");
                throw;
            }
        }

        private async Task EnsureActiveQueueAlarms(
            AlertingGroup group, Queue queue,
            string snsTopic, bool dryRun)
        {
            var lengthThreshold = QueueLengthThreshold(queue, group);

            await _queueAlarmCreator.EnsureLengthAlarm(
                queue.Name, lengthThreshold,
                @group.AlarmNameSuffix, snsTopic, dryRun);

            var oldestMessageThreshold = OldestMessageThreshold(queue, group);

            if (oldestMessageThreshold.HasValue && (lengthThreshold > 1))
            {
                await _queueAlarmCreator.EnsureOldestMessageAlarm(
                    queue.Name, oldestMessageThreshold.Value,
                    @group.AlarmNameSuffix, snsTopic, dryRun);
            }
        }

        private int QueueLengthThreshold(Queue queue, AlertingGroup group)
        {
            if (IsErrorQueue(queue))
            {
                return queue.Errors.LengthThreshold.Value;
            }

            return queue.LengthThreshold ?? group.Sqs.LengthThreshold ?? AwsConstants.QueueLengthThreshold;
        }

        private int? OldestMessageThreshold(Queue queue, AlertingGroup group)
        {
            if (IsErrorQueue(queue))
            {
                return queue.Errors.OldestMessageThreshold;
            }

            return queue.OldestMessageThreshold ?? group.Sqs.OldestMessageThreshold ?? AwsConstants.OldestMessageThreshold;
        }

        private bool IsErrorQueue(Queue queue)
        {
            return
                !string.IsNullOrWhiteSpace(queue.Name) &&
                queue.Name.EndsWith(queue.Errors.Suffix);
        }

        private void ReportPutCounts(bool dryRun)
        {
            if (dryRun)
            {
                if (_queueAlarmCreator.AlarmPutCount > 0)
                {
                    throw new WatchmanException("PUTs happened in dryRun mode");
                }

                _logger.Info("Dry Run: No queue alarms were put");
                return;
            }

            if (_queueAlarmCreator.AlarmPutCount == 0)
            {
                _logger.Info("No queue alarms were put");
            }
            else
            {
                _logger.Info($"Alarms put: {_queueAlarmCreator.AlarmPutCount} queue alarms");
            }
        }
    }
}
