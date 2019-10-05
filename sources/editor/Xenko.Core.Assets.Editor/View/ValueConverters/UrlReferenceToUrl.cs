using System;
using System.Globalization;
using System.Linq;

using Xenko.Core.Assets.Editor.Services;
using Xenko.Core.Assets.Editor.ViewModel;
using Xenko.Core.Serialization;
using Xenko.Core.Serialization.Contents;
using Xenko.Core.Presentation.Quantum.ViewModels;
using Xenko.Core.Translation;
using Xenko.Core.Translation.Presentation.ValueConverters;
using System.ComponentModel;

namespace Xenko.Core.Assets.Editor.View.ValueConverters
{
    public class UrlReferenceToUrl : LocalizableConverter<UrlReferenceToUrl>
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return ContentReferenceHelper.EmptyReference;

            if (!(value is UrlReference reference) || string.IsNullOrWhiteSpace(reference.Url))
                return ContentReferenceHelper.EmptyReference;

            return reference.Url;
        }

        public override object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var url = (string)value;
            var asset = SessionViewModel.Instance.AllAssets.FirstOrDefault(x => x.Url == url);
            if (asset == null)
                return null;

            //Using the generic type so it works in both situtations.
            var contentType = AssetRegistry.GetContentType(asset.AssetType);
            var urlReferenceType = contentType == null ? typeof(UrlReference) : typeof(UrlReference<>).MakeGenericType(contentType);

            return Activator.CreateInstance(urlReferenceType, asset.Id, url);
        }
    }
}