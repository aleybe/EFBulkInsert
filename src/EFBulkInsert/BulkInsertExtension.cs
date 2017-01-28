﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Data.SqlClient;
using System.Reflection;
using EFBulkInsert.Extensions;
using EFBulkInsert.Models;
using System.Linq;
using static System.String;

namespace EFBulkInsert
{
    public static class BulkInsertExtension
    {
        public static void BulkInsert<T>(this DbContext dbContext, IEnumerable<T> entites)
        {
            T[] entitiesArray = entites.ToArray();

            if (entitiesArray.Any())
            {
                EntityMetadata entityMetadata = dbContext.GetEntityMetadata<T>();

                OpenDatabaseConnection(dbContext);

                DataTable dataTable = CreateTempTable<T>(dbContext, entityMetadata);

                InsertDataIntoTempTable(dbContext, entityMetadata, entitiesArray, dataTable);

                DataSet dataSet = MergeDataIntoOriginalTable(dbContext, entityMetadata);

                CopyGeneratedPropertiesToEntities(entityMetadata, dataSet, entitiesArray);

                DropTempTable(dbContext, entityMetadata);
            }
        }

        private static DataTable CreateTempTable<T>(DbContext dbContext, EntityMetadata entityMetadata)
        {
            DataTable dataTable = new DataTable();

            List<string> columns = new List<string>();

            foreach (EntityProperty property in entityMetadata.Properties.Where(x => !x.IsDbGenerated))
            {
                columns.Add($"[{property.ColumnName}] {property.SqlServerType}");

                PropertyInfo propertyInfo = typeof(T).GetProperty(property.PropertyName);

                DataColumn dataColumn = new DataColumn(property.ColumnName)
                {
                    DataType = Nullable.GetUnderlyingType(propertyInfo.PropertyType) ?? propertyInfo.PropertyType,
                    AllowDBNull = property.IsNullable,
                    ColumnName = property.ColumnName
                };

                dataTable.Columns.Add(dataColumn);
            }

            dataTable.Columns.Add("ArrayIndex", typeof(long));
            columns.Add("ArrayIndex bigint");

            string createTableQuery = $"CREATE TABLE {entityMetadata.TempTableName} ({Join(",", columns)})";

            dbContext.Database.ExecuteSqlCommand(createTableQuery);

            return dataTable;
        }

        private static void CopyGeneratedPropertiesToEntities<T>(EntityMetadata entityMetadata, DataSet mergeResult, T[] entities)
        {
            foreach (EntityProperty property in entityMetadata.Properties.Where(x => x.IsDbGenerated))
            {
                for (int i = 0; i < mergeResult.Tables[0].Rows.Count; i++)
                {
                    T entity = entities[(long)mergeResult.Tables[0].Rows[i]["ArrayIndex"]];

                    entity.GetType().GetProperty(property.PropertyName).SetValue(entity, mergeResult.Tables[0].Rows[i][property.ColumnName]);
                }
            }
        }

        private static DataSet MergeDataIntoOriginalTable(DbContext dbContext, EntityMetadata entityMetadata)
        {
            string generatedColumnNames = Join(",", entityMetadata.Properties.Where(x => x.IsDbGenerated).Select(x => $"INSERTED.[{x.ColumnName}]"));

            string columns = Join(",", entityMetadata.Properties.Where(x => !x.IsDbGenerated).Select(x => $"[{x.ColumnName}]"));

            SqlCommand sqlCommand = new SqlCommand($@"MERGE INTO {entityMetadata.TableName} AS DestinationTable
                                                      USING (SELECT * FROM {entityMetadata.TempTableName}) AS TempTable
                                                      ON 1 = 2
                                                      WHEN NOT MATCHED THEN INSERT ({columns}) VALUES ({columns})
                                                      OUTPUT TempTable.ArrayIndex, {generatedColumnNames};", 
                                                   dbContext.GetSqlConnection());

            SqlDataAdapter dataAdapter = new SqlDataAdapter(sqlCommand);
            DataSet dataSet = new DataSet();
            dataAdapter.Fill(dataSet);

            return dataSet;
        }

        private static void InsertDataIntoTempTable<T>(DbContext dbContext, EntityMetadata entityMetadata, T[] entities, DataTable dataTable)
        {
            SqlBulkCopy sqlBulkCopy = new SqlBulkCopy(dbContext.GetSqlConnection())
            {
                DestinationTableName = entityMetadata.TempTableName
            };

            for (int i = 0; i < entities.Length; i++)
            {
                List<object> objects = entityMetadata.Properties.Where(x => !x.IsDbGenerated)
                                                                .Select(property => GetPropertyValueOrDbNull(typeof(T).GetProperty(property.PropertyName).GetValue(entities[i], null)))
                                                                .ToList();

                objects.Add(i);
                dataTable.Rows.Add(objects.ToArray());
            }

            sqlBulkCopy.WriteToServer(dataTable);
        }
        
        private static void DropTempTable(DbContext dbContext, EntityMetadata entityMetadata)
        {
            dbContext.Database.ExecuteSqlCommand($"DROP TABLE {entityMetadata.TempTableName}");
        }

        private static void OpenDatabaseConnection(DbContext dbContext)
        {
            try
            {
                dbContext.Database.Connection.Open();
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private static object GetPropertyValueOrDbNull(object @object)
        {
            return @object ?? DBNull.Value;
        }
    }
}