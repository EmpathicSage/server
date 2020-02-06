﻿using Bit.Core;
using Bit.Core.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;

namespace Bit.Billing.Controllers
{
    [Route("freshdesk")]
    public class FreshdeskController : Controller
    {
        private readonly BillingSettings _billingSettings;
        private readonly IUserRepository _userRepository;
        private readonly IOrganizationRepository _organizationRepository;
        private readonly IOrganizationUserRepository _organizationUserRepository;
        private readonly ILogger<AppleController> _logger;
        private readonly HttpClient _httpClient = new HttpClient();

        public FreshdeskController(
            IUserRepository userRepository,
            IOrganizationRepository organizationRepository,
            IOrganizationUserRepository organizationUserRepository,
            IOptions<BillingSettings> billingSettings,
            ILogger<AppleController> logger)
        {
            _billingSettings = billingSettings?.Value;
            _userRepository = userRepository;
            _organizationRepository = organizationRepository;
            _organizationUserRepository = organizationUserRepository;
            _logger = logger;
        }

        [HttpPost("webhook")]
        public async Task<IActionResult> PostWebhook()
        {
            if(HttpContext?.Request?.Query == null)
            {
                return new BadRequestResult();
            }

            var key = HttpContext.Request.Query.ContainsKey("key") ?
                HttpContext.Request.Query["key"].ToString() : null;
            if(key != _billingSettings.FreshdeskWebhookKey)
            {
                return new BadRequestResult();
            }

            string body = null;
            using(var reader = new StreamReader(HttpContext.Request.Body, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            if(string.IsNullOrWhiteSpace(body))
            {
                return new BadRequestResult();
            }

            try
            {
                dynamic data = JsonConvert.DeserializeObject(body);
                string ticketId = data.ticket_id;
                string ticketContactEmail = data.ticket_contact_email;
                if(string.IsNullOrWhiteSpace(ticketId) || string.IsNullOrWhiteSpace(ticketContactEmail))
                {
                    return new BadRequestResult();
                }

                var updateBody = new Dictionary<string, object>();
                var user = await _userRepository.GetByEmailAsync(ticketContactEmail);
                if(user != null)
                {
                    var tags = new List<string>();
                    if(user.Premium)
                    {
                        tags.Add("Premium");
                    }
                    var orgs = await _organizationRepository.GetManyByUserIdAsync(user.Id);
                    if(orgs.Any())
                    {
                        foreach(var org in orgs)
                        {
                            tags.Add("Org:" + GetAttribute<DisplayAttribute>(org.PlanType).Name);
                        }
                    }
                    var hasPaidOrg = orgs.Any(o => o.PlanType != Core.Enums.PlanType.Free);
                    if(user.Premium || hasPaidOrg)
                    {
                        updateBody.Add("priority", 3);
                    }
                    if(tags.Any())
                    {
                        updateBody.Add("tags", tags);
                    }

                    var request = new HttpRequestMessage(HttpMethod.Put,
                        string.Format("https://bitwarden.freshdesk.com/api/v2/tickets/{0}", ticketId));
                    request.Content = new StringContent(JsonConvert.SerializeObject(updateBody),
                        Encoding.UTF8, "application/json");
                    await CallFreshdeskApiAsync(request);
                }

                return new OkResult();
            }
            catch(Exception e)
            {
                _logger.LogError(e, "Error processing freshdesk webhook.");
                return new BadRequestResult();
            }
        }

        private async Task<HttpResponseMessage> CallFreshdeskApiAsync(HttpRequestMessage request, int retriedCount = 0)
        {
            try
            {
                var authKey = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_billingSettings.FreshdeskApiKey}:X"));
                request.Headers.Add("Authorization", authKey);
                var response = await _httpClient.SendAsync(request);
                if(response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || retriedCount > 3)
                {
                    return response;
                }
            }
            catch
            {
                if(retriedCount > 3)
                {
                    throw;
                }
            }
            await Task.Delay(60000 * (retriedCount + 1));
            return await CallFreshdeskApiAsync(request, retriedCount++);
        }

        private TAttribute GetAttribute<TAttribute>(Enum enumValue) where TAttribute : Attribute
        {
            return enumValue.GetType().GetMember(enumValue.ToString()).First().GetCustomAttribute<TAttribute>();
        }
    }
}