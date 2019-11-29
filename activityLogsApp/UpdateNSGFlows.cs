using System.IO;
using System.Collections.Generic;
using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.WindowsAzure.Storage.Blob;
using System.Net.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Threading;


namespace NwNsgProject
{
    public static class UpdateNSGFlows
    {


		[FunctionName("UpdateNSGFlows")]
		public static async Task Run([TimerTrigger("0 */3 * * * *")] TimerInfo myTimer, TraceWriter log)
		{
		    if(myTimer.IsPastDue)
		    {
		        log.Info("Timer is running late!");
		    }
		    var secret = Environment.GetEnvironmentVariable("MSI_SECRET");
		    var subs_ids = Environment.GetEnvironmentVariable("subscriptionIds").Split(',');
		    string token = null;

		    log.Info($"C# Timer trigger function executed at: {DateTime.Now}");
		    
		    UriBuilder builder = new UriBuilder(Environment.GetEnvironmentVariable("MSI_ENDPOINT"));
			string apiversion = Uri.EscapeDataString("2017-09-01");
			string resource = Uri.EscapeDataString("https://management.azure.com/");
			builder.Query = "api-version="+apiversion+"&resource="+resource;
			log.Info($"url : {builder.Uri}");
			
			var client = new SingleHttpClientInstance();
            try
            {
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.Add("secret", Environment.GetEnvironmentVariable("MSI_SECRET"));

                HttpResponseMessage response = await SingleHttpClientInstance.getToken(req);
                if (response.IsSuccessStatusCode)
				{
				    string data =  await response.Content.ReadAsStringAsync();
				    var tokenObj = JsonConvert.DeserializeObject<Token>(data);
				    token = tokenObj.access_token;
				    log.Info($"bingo : {tokenObj.access_token}");
				}
            }
            catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("Sending to Splunk. Is Splunk service running?", e);
            }

            foreach(var subs_id in subs_ids){
	            ////// get network watchers first

				log.Info($"connected i guess {subs_id}");
				Dictionary<string, string> nwList = new Dictionary<string, string>(); 
				string list_network_watchers = "https://management.azure.com/subscriptions/{0}/providers/Microsoft.Network/networkWatchers?api-version=2018-11-01";
				string list_nsgs = "https://management.azure.com/subscriptions/{0}/providers/Microsoft.Network/networkSecurityGroups?api-version=2018-11-01";
				client = new SingleHttpClientInstance();
	            try
	            {
	                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, String.Format(list_network_watchers, subs_id));
	                req.Headers.Accept.Clear();
	                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
	                
	                log.Info($"reached here 2 {String.Format(list_network_watchers, subs_id)}");
	                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);
	                if (response.IsSuccessStatusCode)
					{
					    string data =  await response.Content.ReadAsStringAsync();
					    var result = JsonConvert.DeserializeObject<NWApiResult>(data);
					    log.Info("converted success");
					    
					    foreach (var nw in result.value) {
					    	nwList.Add(nw.location,nw.name);
					    }
					    
					}
	            }
	            catch (System.Net.Http.HttpRequestException e)
	            {
	                throw new System.Net.Http.HttpRequestException("Sending to Splunk. Is Splunk service running?", e);
	            }


	            ////// get all nsgs

	            client = new SingleHttpClientInstance();
	            try
	            {
	                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, String.Format(list_nsgs, subs_id));
	                req.Headers.Accept.Clear();
	                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
	                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);

	                if (response.IsSuccessStatusCode)
					{
					    string data =  await response.Content.ReadAsStringAsync();
					    var result = JsonConvert.DeserializeObject<NSGApiResult>(data);
					    log.Info("converted success 2");
					   	await enable_flow_logs(result, nwList, token, subs_id, log);
					    log.Info("this is done");
					}
	            } 
	            catch (System.Net.Http.HttpRequestException e)
	            {
	                throw new System.Net.Http.HttpRequestException("Sending to Splunk. Is Splunk service running?", e);
	            }
	        }
			
		}

        public class SingleHttpClientInstance
        {
            private static readonly HttpClient HttpClient;

            static SingleHttpClientInstance()
            {
                HttpClient = new HttpClient();
                HttpClient.Timeout = new TimeSpan(0, 1, 0);
            }

            public static async Task<HttpResponseMessage> getToken(HttpRequestMessage req)
            {
                HttpResponseMessage response = await HttpClient.SendAsync(req);
                return response;
            }

            public static async Task<HttpResponseMessage> sendApiRequest(HttpRequestMessage req, String token)
            {
            	HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                HttpResponseMessage response = await HttpClient.SendAsync(req);
                return response;
            }
            public static async Task<HttpResponseMessage> sendApiPostRequest(HttpRequestMessage req, String token)
            {
            	HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                HttpResponseMessage response = await HttpClient.SendAsync(req);
                return response;
            }

        }

        static async Task enable_flow_logs(NSGApiResult nsgresult, Dictionary<string, string> nwList, String token, String subs_id, TraceWriter log)
        {
        	
        	Dictionary<string, string> storageloc = new Dictionary<string, string>(); 
        	string[] all_locations = new string[]{"eastasia","southeastasia","centralus","eastus","eastus2","westus","northcentralus","southcentralus","northeurope","westeurope","japanwest","japaneast","brazilsouth","australiaeast","australiasoutheast","southindia","centralindia","westindia","canadacentral","canadaeast","uksouth","ukwest","westcentralus","westus2","koreacentral","koreasouth","francecentral","francesouth","australiacentral","australiacentral2"};
        	foreach (var nsg in nsgresult.value) {
		   		string loc_nw = nwList[nsg.location];
		   		log.Info("inside for loop");
		   		string storageId = "";
		   		if(storageloc.ContainsKey(nsg.location)){
		   			storageId = storageloc[nsg.location];
		   		}else{
		   			storageId = await check_avid_storage_account(token,subs_id,nsg.location,log);
		   			storageloc.Add(nsg.location, storageId);
		   		}
		   		log.Info("inside for loop storage check done");
		   		if(storageId.Equals("null")){
		   			log.Info("Done for now");
		   			break;
	   			}else{
	   				enable_flow_request(nsg, storageId, loc_nw, subs_id, token, log);
	   			}
		   	}

		   	Dictionary<string, string> allnsgloc = new Dictionary<string, string>(); 
		   	foreach (var nsg in nsgresult.value) {
		   		if(!allnsgloc.ContainsKey(nsg.location)){
		   			allnsgloc.Add(nsg.location, "yes");
		   		}
		   	}

		   	foreach (string location_check in all_locations){
		   		if(!allnsgloc.ContainsKey(location_check)){
		   			check_delete_storage_account(token, subs_id, location_check ,log);
		   		}
		   	}
        }

        static async Task enable_flow_request(NetworkSecurityGroup nsg, String storageId, String loc_nw, String subs_id, String token, TraceWriter log){
        	string enable_flow_logs_url = "https://management.azure.com/subscriptions/{0}/resourceGroups/NetworkWatcherRG/providers/Microsoft.Network/networkWatchers/{1}/configureFlowLog?api-version=2018-11-01";
        	var client = new SingleHttpClientInstance();
        	try
            {
            	log.Info($"enabling flow log {nsg.id} , {storageId}, {loc_nw}, {subs_id}");
            	dynamic myObject = new JObject();
            	myObject.targetResourceId = nsg.id;
            	dynamic properties = new JObject();
            	properties.storageId = storageId;
            	properties.enabled = true;
            	dynamic retention = new JObject();
            	retention.days = 1;
            	retention.enabled = true;
            	properties.retentionPolicy = retention;
            	myObject.properties = properties;

            	var content = new StringContent(myObject.ToString(), Encoding.UTF8, "application/json");
                
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, String.Format(enable_flow_logs_url, subs_id, loc_nw));
                req.Content = content;
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                HttpResponseMessage response = await SingleHttpClientInstance.sendApiPostRequest(req, token);
                log.Info($"{response}");
                if (response.IsSuccessStatusCode)
				{
				    string data =  await response.Content.ReadAsStringAsync();
				    log.Info($"converted success {nsg.name}");
				}
            } 
            catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("Reuqest Failed?", e);
            }
        }

        static async Task<String> check_avid_storage_account( String token, String subs_id, String location ,TraceWriter log){
        	string fetch_storage_account_details = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Storage/storageAccounts/{2}?api-version=2018-07-01";
        	string customerid = Util.GetEnvironmentVariable("customerId");
        	string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");
        	string local = Util.GetEnvironmentVariable("local");

        	var location_codes_string = @"{""eastasia"":""eea1"",""southeastasia"":""sea1"",""centralus"":""ccu1"",""eastus"":""eeu1"",""eastus2"":""eeu2"",""westus"":""wwu1"",""northcentralus"":""ncu1"",""southcentralus"":""scu1"",""northeurope"":""nne1"",""westeurope"":""wwe1"",""japanwest"":""wwj1"",""japaneast"":""eej1"",""brazilsouth"":""ssb1"",""australiaeast"":""eau1"",""australiasoutheast"":""sau1"",""southindia"":""ssi1"",""centralindia"":""cci1"",""westindia"":""wwi1"",""canadacentral"":""ccc1"",""canadaeast"":""eec1"",""uksouth"":""suk1"",""ukwest"":""wuk1"",""westcentralus"":""wcu1"",""westus2"":""wwu2"",""koreacentral"":""cck1"",""koreasouth"":""ssk1"",""francecentral"":""ccf1"",""francesouth"":""ssf1"",""australiacentral"":""cau1"",""australiacentral2"":""cau2""}";

        	var location_codes = JsonConvert.DeserializeObject<Dictionary<string, string>>(location_codes_string);

        	var subscription_tag = subs_id.Replace("-","").Substring(0,8) + customerid.Replace("-","").Substring(0,8);
        	var storage_account_name = "avi"+local+subscription_tag+location_codes[location];

        	string appNameStage1 = local + "AvidFlowLogs" + subscription_tag  + location_codes[location];
        	string storage_account_name_activity = local + "avidact" + subscription_tag;

        	log.Info($"Creating storage account : {storage_account_name}");
        	try
        	{
        		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, String.Format(fetch_storage_account_details, subs_id, resourceGroup, storage_account_name));
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);
                
                if (response.IsSuccessStatusCode)
				{
				    string data =  await response.Content.ReadAsStringAsync();
				    var result = JsonConvert.DeserializeObject<StorageAccountProp>(data);
				    log.Info("found storage account, checking for deployment");
				    var is_deployment = await check_app_deployment(token, appNameStage1, subs_id, log);
				    if(!is_deployment){
				    	await listKeys(token, storage_account_name, storage_account_name_activity, appNameStage1, subs_id, log);
				    } 
				    return result.id; 	
				    
				}
				else{
					await create_resources(token, subs_id, location_codes[location], storage_account_name, location, log);
					log.Info("returning null for sure");
					log.Info($"{response}");
					return "null";
				}
        	}
        	catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }
        }

        static async Task create_retention_policy(String token, String subs_id, String storage_account_name, TraceWriter log){
        	string policy_url = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Storage/storageAccounts/{2}/managementPolicies/default?api-version=2019-04-01";
			string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");
        	string filled_url = String.Format(policy_url, subs_id, resourceGroup, storage_account_name);
        	string retention_policy_string = @"{""properties"": { ""policy"": { ""rules"": [ { ""name"": ""Sophosflowlogsdelete"", ""enabled"": true, ""type"": ""Lifecycle"", ""definition"": { ""filters"": { ""blobTypes"": [ ""blockBlob"" ] }, ""actions"": { ""baseBlob"": { ""delete"": { ""daysAfterModificationGreaterThan"": 1 } }, ""snapshot"": { ""delete"": { ""daysAfterCreationGreaterThan"": 1 } } } } } ] } } }";
        	var policy_json = JsonConvert.DeserializeObject<StorageAccountRentention>(retention_policy_string);

        	try{

	        	HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Put, filled_url);
	            req.Headers.Accept.Clear();
	            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

	            var content = new StringContent(JsonConvert.SerializeObject(policy_json), Encoding.UTF8, "application/json");
	            req.Content = content;

			    HttpResponseMessage response = await SingleHttpClientInstance.sendApiPostRequest(req, token);
			    
	            if (response.IsSuccessStatusCode)    
	            {
					log.Info("retention policy created successfully");            
	            }
	            else{
	            	log.Info("retention policy request failed");
	            	log.Info($"{response}");
	            }      
	        }
	        catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("retention policy request failed ?", e);
            }

        }

        static async Task create_resources( String token, String subs_id, String location_code, String storage_account_name, String location, TraceWriter log){

        	string create_storage_account_url = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Storage/storageAccounts/{2}?api-version=2018-02-01";
        	
        	string customerid = Util.GetEnvironmentVariable("customerId");
        	string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");
        	string local = Util.GetEnvironmentVariable("local");
        	string storageAccountConnecion = Util.GetEnvironmentVariable("storageAccountConnecion");
        	string avidAddress = Util.GetEnvironmentVariable("avidFlowAddress");

        	var subscription_tag = subs_id.Replace("-","").Substring(0,8) + customerid.Replace("-","").Substring(0,8);
        	string appNameStage1 = local + "AvidFlowLogs" + subscription_tag  + location_code;
        	string storage_account_name_activity = local + "avidact" + subscription_tag;

        	string storage_json_string = @"{""sku"": {""name"": ""Standard_GRS""}, ""kind"": ""StorageV2""  ,""location"": ""eastus""}";
        	var storage_json = JsonConvert.DeserializeObject<StorageAccountPutObj>(storage_json_string);
        	storage_json.location = location;
        	string filled_url = String.Format(create_storage_account_url, subs_id, resourceGroup, storage_account_name);
        	try{

	        	HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Put, filled_url);
	            req.Headers.Accept.Clear();
	            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

	            var content = new StringContent(JsonConvert.SerializeObject(storage_json), Encoding.UTF8, "application/json");
	            req.Content = content;

			    HttpResponseMessage response = await SingleHttpClientInstance.sendApiPostRequest(req, token);
			    
	            if (response.IsSuccessStatusCode)    
	            {
	            	log.Info("going to next step");
	            	int milliseconds = 80000;
					Thread.Sleep(milliseconds);
					log.Info("sleeping for a while");
					create_retention_policy(token, subs_id, storage_account_name, log);
					await listKeys(token, storage_account_name, storage_account_name_activity, appNameStage1, subs_id, log);
	                
	            }
	            else{
	            	log.Info("storage account not created");
	            	log.Info($"{response}");
	            }      
	        }
	        catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }
        }

        static async Task listKeys(String token, String storage_account_name, String storage_account_name_activity, String appNameStage1, String subs_id, TraceWriter log){
        	string list_storage_account_keys = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Storage/storageAccounts/{2}/listKeys?api-version=2018-07-01";
        	string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");

            HttpRequestMessage list_req = new HttpRequestMessage(HttpMethod.Post, String.Format(list_storage_account_keys, subs_id, resourceGroup, storage_account_name));
            list_req.Headers.Accept.Clear();
            list_req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage response_keys = await SingleHttpClientInstance.sendApiRequest(list_req, token);
            
            string data_check =  await response_keys.Content.ReadAsStringAsync();
            
            if (response_keys.IsSuccessStatusCode)
			{
			    string data =  await response_keys.Content.ReadAsStringAsync();
			    var result = JsonConvert.DeserializeObject<StorageAccountKeyList>(data);
			    log.Info("found storage account string");
			    string accountkey = "";
			   	foreach(var key in result.keys){
			   		accountkey = key.value;
			   	}

			   	Boolean check_deploy = await deploy_app(token, accountkey , storage_account_name, storage_account_name_activity, appNameStage1, subs_id, log);
			   	log.Info($"deplyoment successful {check_deploy}");
			}
			else{
				log.Info($"keys {response_keys}");
                log.Info($"keys {storage_account_name}");
                log.Info($"keys {String.Format(list_storage_account_keys, subs_id, resourceGroup, storage_account_name)}");
                log.Info($"keys data {data_check}");
                log.Info("Couldnot list keys");
			}
        }

        static async Task<Boolean> deploy_app(String token, String accountkey , String storage_account_name, String storage_account_name_activity, String appNameStage1, String subs_id, TraceWriter log){
        	log.Info("app deployment 1111");
        	string connectionString = "DefaultEndpointsProtocol=https;AccountName={0};AccountKey={1}";
        	string create_deployment_url = "https://management.azure.com/subscriptions/{0}/resourcegroups/{1}/providers/Microsoft.Resources/deployments/{2}?api-version=2018-05-01";
        	string filledConnectionString = String.Format(connectionString, storage_account_name, accountkey);
        	string customerid = Util.GetEnvironmentVariable("customerId");
        	string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");
        	string local = Util.GetEnvironmentVariable("local");
        	string storageAccountConnecion = Util.GetEnvironmentVariable("storageAccountConnecion");
        	string avidAddress = Util.GetEnvironmentVariable("avidFlowAddress");
        	log.Info("2222");
		   	string deployment_json_string = @"{""properties"": {""templateLink"": {""uri"": ""https://s3.us-east-2.amazonaws.com/onboardstaging/azureflows/azureFlowDeploy.json"",""contentVersion"": ""1.0.0.0""},""mode"": ""Incremental"",""parameters"": {""customerId"": {""value"": ""null""},""nsgSourceDataConnection"":{""value"":""null""},""storageAccountName"":{""value"":""null""},""storageAccountConnecion"":{""value"":""null""},""appName"":{""value"":""null""},""avidAddress"":{""value"":""null""} } } }";
		    log.Info("3333");
		    var deployment_json = JsonConvert.DeserializeObject<WebAppDeployment>(deployment_json_string);
		    log.Info("4444");
		    deployment_json.properties.templateLink.uri = Util.GetEnvironmentVariable("flowDepSource");
		    deployment_json.properties.parameters.customerId.value = customerid;
		    deployment_json.properties.parameters.nsgSourceDataConnection.value = filledConnectionString;
		    deployment_json.properties.parameters.storageAccountName.value = storage_account_name_activity;
		    deployment_json.properties.parameters.appName.value = appNameStage1;
		    deployment_json.properties.parameters.storageAccountConnecion.value = storageAccountConnecion;
		    deployment_json.properties.parameters.avidAddress.value = avidAddress;

		    string filled_url = String.Format(create_deployment_url, subs_id, resourceGroup, appNameStage1);

		    try{
			    HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Put, filled_url);
	            req.Headers.Accept.Clear();
	            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

	            var content = new StringContent(JsonConvert.SerializeObject(deployment_json), Encoding.UTF8, "application/json");
	            req.Content = content;

			    HttpResponseMessage response = await SingleHttpClientInstance.sendApiPostRequest(req, token);
			    
			    var check_resp = await response.Content.ReadAsStringAsync();
			    
			    if (response.IsSuccessStatusCode){
			    	return true;
			    }else{
			    	log.Info($"dep {response}");
			    	log.Info($"{check_resp}");
			    }
			}
			catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }
		    return false;
        }

        static async Task<Boolean> check_app_deployment(String token, String appNameStage1, String subs_id, TraceWriter log){
        	string check_deployment_url = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Web/sites/{2}/functions?api-version=2016-08-01";
        	string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");
        	try
        	{
        		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, String.Format(check_deployment_url, subs_id, resourceGroup, appNameStage1));
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);
                log.Info("app deployment checking");
                
                if (response.IsSuccessStatusCode)
				{
				    return true;
				}else{
					log.Info($"{response}");
				}
        	}
        	catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }
            return false;

        }

        static async Task deploy_zip(String appNameStage1, TraceWriter log){
        	string deploy_func = "https://{0}.scm.azurewebsites.net/api/zipdeploy";
	    	string filled_deploy_func = String.Format(deploy_func, appNameStage1);
	    	string zipDeplymentUri = "";
	    	string token = await get_token_net(log);
	    	try{
		    	HttpRequestMessage final_req = new HttpRequestMessage(HttpMethod.Put, filled_deploy_func);
	            final_req.Headers.Accept.Clear();
	            final_req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
	            
	            dynamic myObject = new JObject();
	        	myObject.packageUri = zipDeplymentUri;
	        	var content = new StringContent(myObject.ToString(), Encoding.UTF8, "application/json");

	        	final_req.Content = content;
	            HttpResponseMessage response = await SingleHttpClientInstance.sendApiPostRequest(final_req, token);
	            
	            var check_resp = await response.Content.ReadAsStringAsync();
			    
	            if (response.IsSuccessStatusCode)
				{
					log.Info("All resources created successfully");	
				}else{
					log.Info($"func {response}");
					log.Info($"{check_resp}");
				}
			}
			catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }
        }

        static async Task<String> get_token_net(TraceWriter log){
        	var secret = Environment.GetEnvironmentVariable("MSI_SECRET");
        	UriBuilder builder = new UriBuilder(Environment.GetEnvironmentVariable("MSI_ENDPOINT"));
			string apiversion = Uri.EscapeDataString("2017-09-01");

			string resource = Uri.EscapeDataString("https://management.core.windows.net/");
			builder.Query = "api-version="+apiversion+"&resource="+resource;
			
			var client = new SingleHttpClientInstance();
            try
            {
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.Add("secret", Environment.GetEnvironmentVariable("MSI_SECRET"));

                HttpResponseMessage response = await SingleHttpClientInstance.getToken(req);
                if (response.IsSuccessStatusCode)
				{
				    string data =  await response.Content.ReadAsStringAsync();
				    var tokenObj = JsonConvert.DeserializeObject<Token>(data);
				    String token = tokenObj.access_token;
				    return token;
				}
            }
            catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("Sending to Splunk. Is Splunk service running?", e);
            }
            return "";
        }

        static async Task remove_resources(String storage_account_name, String app_name, String token, String subs_id, String location,TraceWriter log){
        	string storage_account_delete_url = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Storage/storageAccounts/{2}?api-version=2019-04-01";
        	string webapp_delete_url = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Web/sites/{2}?deleteMetrics=true&deleteEmptyServerFarm=true&api-version=2016-08-01";
			string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");

        	try
        	{
        		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Delete, String.Format(storage_account_delete_url, subs_id, resourceGroup, storage_account_name));
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);

                if (response.IsSuccessStatusCode)
				{
					log.Info($"storage account deleted");
				}else{
					log.Info($"storage account deletion failed {response}");
				}
			}
			catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }

            try
        	{
        		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Delete, String.Format(webapp_delete_url, subs_id, resourceGroup, app_name));
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);

                if (response.IsSuccessStatusCode)
				{
					log.Info($"app deleted");
				}else{
					log.Info($"app deletion failed {response}");
				}
			}
			catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }
        }

        static async Task check_delete_storage_account( String token, String subs_id, String location ,TraceWriter log){
        	string fetch_storage_account_details = "https://management.azure.com/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Storage/storageAccounts/{2}?api-version=2018-07-01";
        	string customerid = Util.GetEnvironmentVariable("customerId");
        	string resourceGroup = Util.GetEnvironmentVariable("avidResourceGroup");
        	string local = Util.GetEnvironmentVariable("local");

        	var location_codes_string = @"{""eastasia"":""eea1"",""southeastasia"":""sea1"",""centralus"":""ccu1"",""eastus"":""eeu1"",""eastus2"":""eeu2"",""westus"":""wwu1"",""northcentralus"":""ncu1"",""southcentralus"":""scu1"",""northeurope"":""nne1"",""westeurope"":""wwe1"",""japanwest"":""wwj1"",""japaneast"":""eej1"",""brazilsouth"":""ssb1"",""australiaeast"":""eau1"",""australiasoutheast"":""sau1"",""southindia"":""ssi1"",""centralindia"":""cci1"",""westindia"":""wwi1"",""canadacentral"":""ccc1"",""canadaeast"":""eec1"",""uksouth"":""suk1"",""ukwest"":""wuk1"",""westcentralus"":""wcu1"",""westus2"":""wwu2"",""koreacentral"":""cck1"",""koreasouth"":""ssk1"",""francecentral"":""ccf1"",""francesouth"":""ssf1"",""australiacentral"":""cau1"",""australiacentral2"":""cau2""}";
        	var location_codes = JsonConvert.DeserializeObject<Dictionary<string, string>>(location_codes_string);

        	var subscription_tag = subs_id.Replace("-","").Substring(0,8) + customerid.Replace("-","").Substring(0,8);
        	var storage_account_name = "avi"+local+subscription_tag+location_codes[location];

        	string appNameStage1 = local + "AvidFlowLogs" + subscription_tag  + location_codes[location];

        	
        	try
        	{
        		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, String.Format(fetch_storage_account_details, subs_id, resourceGroup, storage_account_name));
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response = await SingleHttpClientInstance.sendApiRequest(req, token);
                
                if (response.IsSuccessStatusCode)
				{
					log.Info($"deleting storage account : {storage_account_name}");
					string data =  await response.Content.ReadAsStringAsync();
				    var result = JsonConvert.DeserializeObject<StorageAccountProp>(data);
				    remove_resources(storage_account_name, appNameStage1, token, subs_id, location, log);
				}
			}
			catch (System.Net.Http.HttpRequestException e)
            {
                throw new System.Net.Http.HttpRequestException("request failed ?", e);
            }
        }
	}
}