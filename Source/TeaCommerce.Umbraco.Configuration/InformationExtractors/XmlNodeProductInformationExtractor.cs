using Autofac;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Dependency;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Umbraco.Configuration.Services;
using umbraco;
using Umbraco.Core;
using Umbraco.Core.Models;
using Umbraco.Web;
using Constants = TeaCommerce.Api.Constants;

namespace TeaCommerce.Umbraco.Configuration.InformationExtractors {
  public class XmlNodeProductInformationExtractor : IXmlNodeProductInformationExtractor {

    protected readonly IStoreService StoreService;
    protected readonly ICurrencyService CurrencyService;
    protected readonly IVatGroupService VatGroupService;

    public static IXmlNodeProductInformationExtractor Instance { get { return DependencyContainer.Instance.Resolve<IXmlNodeProductInformationExtractor>(); } }

    public XmlNodeProductInformationExtractor( IStoreService storeService, ICurrencyService currencyService, IVatGroupService vatGroupService ) {
      StoreService = storeService;
      CurrencyService = currencyService;
      VatGroupService = vatGroupService;
    }

    public virtual string GetPropertyValue( XPathNavigator model, string variantGuid, string propertyAlias, string selector = null, bool useCachedInformation = true ) {
      string propertyValue = "";

      if ( !string.IsNullOrEmpty( variantGuid ) ) {
        string nodeIdStr = GetPropertyValue( model, null, "@id" );
        int nodeId = 0;
        if ( !string.IsNullOrEmpty( nodeIdStr ) && int.TryParse( nodeIdStr, out nodeId ) ) {
          IPublishedContent variant = VariantService.Instance.GetVariants( nodeId, variantGuid );
          if ( variant != null ) {
            //TODO: Hvad sker der hvis det er en avanceret type
            propertyValue = variant.GetPropertyValue( propertyAlias ).ToString();
          }
        }
      }

      if ( string.IsNullOrEmpty( propertyValue ) ) {
        XPathNavigator xmlProperty = GetXmlPropertyValue( model, propertyAlias, selector, useCachedInformation );
        if ( xmlProperty != null ) {
          propertyValue = xmlProperty.Value;
        }
      }

      return propertyValue;
    }

    public virtual XPathNavigator GetXmlPropertyValue( XPathNavigator model, string propertyAlias, string selector = null, bool useCachedInformation = true ) {
      //Check if this node or ancestor has it
      XPathNavigator xmlProperty = GetXmlPropertyValueInternal( model, propertyAlias, selector, useCachedInformation );

      //Check if we found the value
      if ( xmlProperty == null ) {

        //Check if we can find a master relation
        XPathNavigator masterRelationNodeId = GetXmlPropertyValueInternal( model, null, Constants.ProductPropertyAliases.MasterRelationPropertyAlias, useCachedInformation: useCachedInformation );
        if ( masterRelationNodeId != null ) {
          XPathNodeIterator masterRelation = library.GetXmlNodeById( masterRelationNodeId.Value );
          if ( masterRelation != null ) {
            xmlProperty = GetXmlPropertyValue( masterRelation.Current, propertyAlias, selector, useCachedInformation );
          }
        }

      }

      return xmlProperty;
    }

    protected virtual XPathNavigator GetXmlPropertyValueInternal( XPathNavigator model, string propertyAlias, string selector = null, bool useCachedInformation = true ) {
      XPathNavigator navigator = null;

      if ( model != null && !string.IsNullOrEmpty( propertyAlias ) ) {
        string propertySelector = !UmbracoSettings.UseLegacyXmlSchema || propertyAlias.IndexOf( '@' ) == 0 ? propertyAlias : "data[@alias = '" + propertyAlias + "']";
        string xpath = string.Format( "./ancestor-or-self::* [string({0}) != ''{1}][1]/{0}", propertySelector, !string.IsNullOrEmpty( selector ) ? " and " + selector : "" );

        bool nodePublished = model.SelectSingleNode( "./error" ) == null;

        if ( useCachedInformation && nodePublished ) {
          navigator = model.SelectSingleNode( xpath );
        } else {
          //The node isnt published or we want to use uncached info (saving a Umbraco node) - we try and fetch the info one node at a time until we find a published node
          try {
            IContent content = ApplicationContext.Current.Services.ContentService.GetById( int.Parse( !nodePublished ? Regex.Match( model.Value, @"\d+" ).Value : model.SelectSingleNode( "@id" ).Value ) );

            if ( !propertyAlias.StartsWith( "@" ) ) {
              Property property = content.Properties.SingleOrDefault( p => p.Alias == propertyAlias );
              if ( property != null ) {
                string propertyValue = property.Value.ToString();
                if ( !string.IsNullOrEmpty( propertyValue ) ) {
                  navigator = ( propertyValue.StartsWith( "<" ) && propertyValue.EndsWith( ">" ) ? XElement.Parse( property.Value.ToString() ) : new XElement( "value", propertyValue ) ).CreateNavigator().SelectSingleNode( "/" );
                }
              }
            } else {
              if ( propertyAlias == "@id" ) {
                navigator = new XElement( "value", content.Id ).CreateNavigator().SelectSingleNode( "/" );
              } else if ( propertyAlias == "@nodeName" ) {
                navigator = new XElement( "value", content.Name ).CreateNavigator().SelectSingleNode( "/" );
              } else if ( propertyAlias == "@path" ) {
                navigator = new XElement( "value", content.Path ).CreateNavigator().SelectSingleNode( "/" );
              }
            }

            if ( navigator == null && content.ParentId != -1 ) {
              navigator = GetXmlPropertyValueInternal( library.GetXmlNodeById( content.ParentId.ToString( CultureInfo.InvariantCulture ) ).Current, propertyAlias, selector, useCachedInformation );
            }
          } catch ( Exception ) {
          }
        }
      }

      return navigator;
    }

    public virtual long GetStoreId( XPathNavigator model, bool useCachedInformation = true ) {
      long? storeId = GetPropertyValue( model, null, Constants.ProductPropertyAliases.StorePropertyAlias, useCachedInformation: useCachedInformation ).TryParse<long>();
      if ( storeId == null ) {
        throw new ArgumentException( "The model doesn't have a store id associated with it - remember to add the Tea Commerce store picker to your Umbraco content tree" );
      }

      return storeId.Value;
    }

    public virtual string GetSku( XPathNavigator model, string variantGuid, bool useCachedInformation = true ) {
      string sku = GetPropertyValue( model, variantGuid, Constants.ProductPropertyAliases.SkuPropertyAlias, useCachedInformation: useCachedInformation );

      //If no sku is found - default to umbraco node id
      if ( string.IsNullOrEmpty( sku ) ) {
        string variantId = !string.IsNullOrEmpty( variantGuid ) ? "_" + variantGuid : "";
        sku = GetPropertyValue( model, null, "@id", useCachedInformation: useCachedInformation ) + variantId;
      }

      return sku;
    }

    public virtual string GetName( XPathNavigator model, string variantGuid, bool useCachedInformation = true ) {
      string name = GetPropertyValue( model, variantGuid, Constants.ProductPropertyAliases.NamePropertyAlias, useCachedInformation: useCachedInformation );

      //If no name is found - default to the umbraco node name
      if ( string.IsNullOrEmpty( name ) ) {
        name = GetPropertyValue( model, null, "@nodeName", useCachedInformation: useCachedInformation );
      }

      return name;
    }

    public virtual long? GetVatGroupId( XPathNavigator model, string variantGuid, bool useCachedInformation = true ) {
      long storeId = GetStoreId( model, useCachedInformation );
      long? vatGroupId = GetPropertyValue( model, variantGuid, Constants.ProductPropertyAliases.VatGroupPropertyAlias, useCachedInformation: useCachedInformation ).TryParse<long>();

      //In umbraco a product can have a relation to a delete marked vat group
      if ( vatGroupId != null ) {
        VatGroup vatGroup = VatGroupService.Get( storeId, vatGroupId.Value );
        if ( vatGroup == null || vatGroup.IsDeleted ) {
          vatGroupId = null;
        }
      }

      return vatGroupId;
    }

    public virtual long? GetLanguageId( XPathNavigator model, bool useCachedInformation = true ) {
      return LanguageService.Instance.GetLanguageIdByNodePath( GetPropertyValue( model, null, "@path", useCachedInformation: useCachedInformation ) );
    }

    public virtual OriginalUnitPriceCollection GetOriginalUnitPrices( XPathNavigator model, string variantGuid, bool useCachedInformation = true ) {
      OriginalUnitPriceCollection prices = new OriginalUnitPriceCollection();

      foreach ( Currency currency in CurrencyService.GetAll( GetStoreId( model, useCachedInformation ) ) ) {
        prices.Add( new OriginalUnitPrice( GetPropertyValue( model, variantGuid, currency.PricePropertyAlias, useCachedInformation: useCachedInformation ).ParseToDecimal() ?? 0M, currency.Id ) );
      }

      return prices;
    }

    public virtual CustomPropertyCollection GetProperties( XPathNavigator model, string variantGuid, bool useCachedInformation = true ) {
      CustomPropertyCollection properties = new CustomPropertyCollection();

      foreach ( string productPropertyAlias in StoreService.Get( GetStoreId( model, useCachedInformation ) ).ProductSettings.ProductPropertyAliases ) {
        properties.Add( new CustomProperty( productPropertyAlias, GetPropertyValue( model, variantGuid, productPropertyAlias, useCachedInformation: useCachedInformation ) ) { IsReadOnly = true } );
      }

      return properties;
    }

    public virtual ProductSnapshot GetSnapshot( XPathNavigator model, string variantGuid, string productIdentifier, bool useCachedInformation = true ) {
      //We use Clone() because each method should have it's own instance of the navigator - so if they traverse it doesn't affect other methods
      ProductSnapshot snapshot = new ProductSnapshot( GetStoreId( model.Clone(), useCachedInformation ), productIdentifier ) {
        Sku = GetSku( model.Clone(), variantGuid, useCachedInformation ),
        Name = GetName( model.Clone(), variantGuid, useCachedInformation ),
        VatGroupId = GetVatGroupId( model.Clone(), variantGuid, useCachedInformation ),
        LanguageId = GetLanguageId( model.Clone(), useCachedInformation ),
        OriginalUnitPrices = GetOriginalUnitPrices( model.Clone(), variantGuid, useCachedInformation ),
        Properties = GetProperties( model.Clone(), variantGuid, useCachedInformation )
      };

      return snapshot;
    }

    public virtual bool HasAccess( long storeId, XPathNavigator model, bool useCachedInformation = true ) {
      return storeId == GetStoreId( model ) && library.HasAccess( int.Parse( GetPropertyValue( model, null, "@id", useCachedInformation: useCachedInformation ) ), GetPropertyValue( model, null, "@path", useCachedInformation: useCachedInformation ) );
    }
  }
}
