using System;
using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using Audit.Core;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson.IO;
using Newtonsoft.Json;
using JsonConvert = Newtonsoft.Json.JsonConvert;

namespace Audit.MongoDB.Providers
{
    /// <summary>
    /// Mongo DB data access
    /// </summary>
    /// <remarks>
    /// Settings:
    /// - ConnectionString: Mongo connection string
    /// - Database: Database name
    /// - Collection: Collection name
    /// - IgnoreElementNames: indicate whether the element names should be validated and fixed or not
    /// </remarks>
    public class MongoDataProvider : AuditDataProvider
    {
        private string _connectionString = "mongodb://localhost:27017";
        private string _database = "Audit";
        private string _collection = "Event";
        private bool _ignoreElementNames = false;
        private JsonSerializerSettings _jsonSerializerSettings =  new JsonSerializerSettings() { ReferenceLoopHandling = ReferenceLoopHandling.Ignore };
        private JsonWriterSettings _jsonWriterSettings = new JsonWriterSettings { OutputMode = JsonOutputMode.Strict };

        static MongoDataProvider()
        {
            ConfigureBsonMapping();
        }

        /// <summary>
        /// Gets or sets the MongoDB connection string.
        /// </summary>
        public string ConnectionString
        {
            get { return _connectionString; }
            set { _connectionString = value; }
        }

        /// <summary>
        /// Gets or sets the MongoDB Database name.
        /// </summary>
        public string Database
        {
            get { return _database; }
            set { _database = value; }
        }

        /// <summary>
        /// Gets or sets the MongoDB collection name.
        /// </summary>
        public string Collection
        {
            get { return _collection; }
            set { _collection = value; }
        }

        /// <summary>
        /// Gets or sets a value to indicate whether the element names should be validated/fixed or not.
        /// If <c>true</c> the element names are not validated, use this when you know the element names will not contain invalid characters.
        /// If <c>false</c> (default) the element names are validated and fixed to avoid containing invalid characters.
        /// </summary>
        public bool IgnoreElementNames
        {
            get { return _ignoreElementNames; }
            set { _ignoreElementNames = value; }
        }

        /// <summary>
        /// Gets or sets the default JsonSerializerSettings.
        /// </summary>
        public JsonSerializerSettings JsonSerializerSettings
        {
            get { return _jsonSerializerSettings; }
            set { _jsonSerializerSettings = value; }
        }

        private static void ConfigureBsonMapping()
        {
            var pack = new ConventionPack();
            pack.Add(new IgnoreIfNullConvention(true));
            ConventionRegistry.Register("Ignore null properties for AuditEvent", pack, type => type == typeof(AuditEvent));

            BsonClassMap.RegisterClassMap<AuditTarget>(cm =>
            {
                cm.AutoMap();
                cm.MapProperty(x => x.SerializedOld).SetElementName("Old");
                cm.MapProperty(x => x.SerializedNew).SetElementName("New");
            });

            BsonClassMap.RegisterClassMap<AuditEvent>(cm =>
            {
               cm.AutoMap();
               cm.MapExtraElementsField(c => c.CustomFields);
            });
        }

        public override object InsertEvent(AuditEvent auditEvent)
        {
            var db = GetDatabase();
            var col = db.GetCollection<BsonDocument>(_collection);
            SerializeExtraFields(auditEvent);
            var doc = ParseBson(auditEvent);
            if (!_ignoreElementNames)
            {
                FixDocumentElementNames(doc);
            }
            col.InsertOne(doc);
            return (BsonObjectId)doc["_id"];
        }

        public override void ReplaceEvent(object eventId, AuditEvent auditEvent)
        {
            var db = GetDatabase();
            var col = db.GetCollection<BsonDocument>(_collection);
            SerializeExtraFields(auditEvent);
            var doc = ParseBson(auditEvent);
            if (!_ignoreElementNames)
            {
                FixDocumentElementNames(doc);
            }
            var filter = Builders<BsonDocument>.Filter.Eq("_id", (BsonObjectId)eventId);
            col.ReplaceOne(filter, doc);
        }

        private void SerializeExtraFields(AuditEvent auditEvent)
        {
            foreach(var k in auditEvent.CustomFields.Keys.ToList())
            {
                auditEvent.CustomFields[k] = Serialize(auditEvent.CustomFields[k]);
            }
        }

        private BsonDocument ParseBson(AuditEvent auditEvent)
        {
            return BsonDocument.Parse(JsonConvert.SerializeObject(auditEvent, _jsonSerializerSettings));
        }

        /// <summary>
        /// Fixes the document Element Names (avoid using dots '.' and starting with '$').
        /// </summary>
        /// <param name="document">The document to fix.</param>
        private void FixDocumentElementNames(BsonDocument document)
        {
            var toRename = new List<Tuple<string, BsonValue, string>>();
            foreach (var elem in document)
            {
                if (elem.Name.Contains(".") || elem.Name.StartsWith("$"))
                {
                    var value = elem.Value;
                    var name = elem.Name.Replace('.', '_');
                    if (name.StartsWith("$"))
                    {
                        name = "_" + name.Substring(1);
                    }
                    toRename.Add(new Tuple<string, BsonValue, string>(elem.Name, value, name));
                }
                if (elem.Value != null)
                {
                    if (elem.Value.IsBsonDocument)
                    {
                        FixDocumentElementNames(elem.Value as BsonDocument);
                    }
                    else if (elem.Value.IsBsonArray)
                    {
                        foreach (var sub in (elem.Value as BsonArray))
                        {
                            if (sub.IsBsonDocument)
                            {
                                FixDocumentElementNames(sub as BsonDocument);
                            }
                        }
                    }
                }
            }
            foreach (var x in toRename)
            {
                document.Remove(x.Item1);
                document.Add(new BsonElement(x.Item3, x.Item2));
            }
        }

        public override object Serialize<T>(T value)
        {
            if (value == null)
            {
                return null;
            }
            return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value, _jsonSerializerSettings));
        }

        private void TestConnection()
        {
            var db = GetDatabase();
            var test = db.RunCommand((Command<BsonDocument>)"{ping:1}");
            
            if (test["ok"].ToInt64() != 1)
            {
                throw new Exception("Can't connect to Audit Mongo Database.");
            }
        }

        private IMongoDatabase GetDatabase()
        {
            var client = new MongoClient(_connectionString);
            var db = client.GetDatabase(_database);
            return db;
        }

        public override T GetEvent<T>(object eventId)
        {
            var client = new MongoClient(_connectionString);
            var db = client.GetDatabase(_database);
            var filter = Builders<BsonDocument>.Filter.Eq("_id", (BsonObjectId)eventId);
            var doc = db.GetCollection<BsonDocument>(_collection).Find(filter).FirstOrDefault();
            return doc == null ? null : JsonConvert.DeserializeObject<T>(doc.ToJson(_jsonWriterSettings), _jsonSerializerSettings);
        }

        public override async Task<T> GetEventAsync<T>(object eventId)
        {
            var client = new MongoClient(_connectionString);
            var db = client.GetDatabase(_database);
            var filter = Builders<BsonDocument>.Filter.Eq("_id", (BsonObjectId)eventId);
            var doc = await (await db.GetCollection<BsonDocument>(_collection).FindAsync(filter)).FirstOrDefaultAsync();
            return doc == null ? null : JsonConvert.DeserializeObject<T>(doc.ToJson(_jsonWriterSettings), _jsonSerializerSettings);
        }

        #region Events Query        
        /// <summary>
        /// Returns an IQueryable that enables querying against the audit events stored on Azure Document DB.
        /// </summary>
        public IQueryable<AuditEvent> QueryEvents()
        {
            var client = new MongoClient(_connectionString);
            var db = client.GetDatabase(_database);
            return db.GetCollection<AuditEvent>(_collection).AsQueryable();
        }
        /// <summary>
        /// Returns an IQueryable that enables querying against the audit events stored on Azure Document DB, for the audit event type given.
        /// </summary>
        /// <typeparam name="T">The AuditEvent type</typeparam>
        public IQueryable<T> QueryEvents<T>() where T : AuditEvent
        {
            var client = new MongoClient(_connectionString);
            var db = client.GetDatabase(_database);
            return db.GetCollection<T>(_collection).AsQueryable();
        }
        /// <summary>
        /// Returns a native mongo collection of audit events
        /// </summary>
        public IMongoCollection<AuditEvent> GetMongoCollection()
        {
            var client = new MongoClient(_connectionString);
            var db = client.GetDatabase(_database);
            return db.GetCollection<AuditEvent>(_collection);
        }
        /// <summary>
        /// Returns a native mongo collection of audit events
        /// </summary>
        public IMongoCollection<T> GetMongoCollection<T>() where T : AuditEvent
        {
            var client = new MongoClient(_connectionString);
            var db = client.GetDatabase(_database);
            return db.GetCollection<T>(_collection);
        }
        #endregion
    }
}