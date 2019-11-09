﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Threading.Tasks;
using DurableTask.AzureStorage.Monitoring;
using Dynamitey.Internal.Optimization;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Keda.Durable.Scaler.Server.Protos;
using Keda.Durable.Scaler.Server.Repository;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Keda.Durable.Scaler.Server.Services
{
    public class DurableScalerConfig
    {
        public string Namespace { get; set; }
        public string DeploymentName { get; set; }
    }
    public class ExternalScalerService : ExternalScaler.ExternalScalerBase
    {
        private const string ScaleRecommendation = "ScaleRecommendation";
        private IPerformanceMonitorRepository _performanceMonitorRepository;
        private IKubernetesRepository _kubernetesRepository;
        private readonly ILogger<ExternalScalerService> _logger;
        private ConcurrentDictionary<string, DurableScalerConfig> _scalers;
        public ExternalScalerService(IPerformanceMonitorRepository performanceMonitorRepository, IKubernetesRepository kubernetesRepository, ILogger<ExternalScalerService> logger)
        {
            _performanceMonitorRepository = performanceMonitorRepository;
            _kubernetesRepository = kubernetesRepository;
            _logger = logger;
        }
        public override Task<Empty> New(NewRequest request, ServerCallContext context)
        {
            _logger.LogInformation($"Namespace: {request?.ScaledObjectRef?.Namespace} DeploymentName: {request?.ScaledObjectRef?.Name} New() called.");
            var settings = new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            };
            var requestOjbect = JsonConvert.SerializeObject(request, settings); 
            var contextObject = JsonConvert.SerializeObject(context, settings);
            _logger.LogDebug("******* requestObject");
            _logger.LogDebug(requestOjbect);
            _logger.LogDebug("***** contextObject");
            _logger.LogDebug(contextObject);
    
            var scaler = new DurableScalerConfig
            {
                Namespace = request.ScaledObjectRef.Namespace,
                DeploymentName = request.ScaledObjectRef.Name
            };
            _scalers.TryAdd(GetScalerUniqueName(request.ScaledObjectRef), scaler);

            return Task.FromResult(new Empty());
        }

        private string GetScalerUniqueName(ScaledObjectRef scaleObjectRef)
        {
            return $"{scaleObjectRef.Namespace}/{scaleObjectRef.Name}";
        }

        public override async Task<IsActiveResponse> IsActive(ScaledObjectRef request, ServerCallContext context)
        {
            _logger.LogInformation($"Namespace: {request?.Namespace} DeploymentName: {request?.Name} IsActive() called.");
            // True or false if the deployment work in progress. 
            var heartbeat = await _performanceMonitorRepository.PulseAsync(await GetCurrentWorkerCountAsync(_scalers[GetScalerUniqueName(request)]));
            var response = new IsActiveResponse();
            response.Result = true;
            return response;
        }

        public override Task<GetMetricSpecResponse> GetMetricSpec(ScaledObjectRef request, ServerCallContext context)
        {
            _logger.LogInformation($"Namespace: {request?.Namespace} DeploymentName: {request?.Name} GetMetricSpec() called.");
            var response = new GetMetricSpecResponse();
            var fields = new RepeatedField<MetricSpec>();
            fields.Add(new MetricSpec()
            {
                MetricName = ScaleRecommendation,
                TargetSize = 5
            });
            response.MetricSpecs.Add(fields);
            return Task.FromResult(response);
        }

        public override async Task<GetMetricsResponse> GetMetrics(GetMetricsRequest request, ServerCallContext context)
        {
            _logger.LogInformation($"Namespace: {request?.ScaledObjectRef?.Namespace} DeploymentName: {request?.ScaledObjectRef?.Name} GetMetrics() called.");
            var heartbeat = await _performanceMonitorRepository.PulseAsync(await GetCurrentWorkerCountAsync(_scalers[GetScalerUniqueName(request.ScaledObjectRef)]));
            int targetSize = 0;
            switch (heartbeat.ScaleRecommendation.Action)
            {
                case ScaleAction.AddWorker:
                    targetSize = 9;
                    break;
                case ScaleAction.RemoveWorker:
                    targetSize = 1;
                    break;
                default:
                    break;
            }
            var res = new GetMetricsResponse();
            var metricValue = new MetricValue
            {
                MetricName = ScaleRecommendation,
                MetricValue_ = targetSize
            };
            res.MetricValues.Add(metricValue);
            return res;
            // Return the value that is 
            //var res = new GetMetricsResponse();
            //var metricValue = new MetricValue();
            //metricValue.MetricName = "hello";
            //metricValue.MetricValue_ = 10;
            //res.MetricValues.Add(metricValue);
            //return Task.FromResult(res);
        }

        public override Task<Empty> Close(ScaledObjectRef request, ServerCallContext context)
        {
            _logger.LogInformation($"Namespace: {request?.Namespace} DeploymentName: {request?.Name} Close() called.");
            _scalers.TryRemove(GetScalerUniqueName(request), out DurableScalerConfig config);
            // We don't need to do something in here. 
            return Task.FromResult(new Empty());
        }

        private Task<int> GetCurrentWorkerCountAsync(DurableScalerConfig config)
        {
            return _kubernetesRepository.GetNumberOfPodAsync(config.DeploymentName, config.Namespace);
        }

    }
}
