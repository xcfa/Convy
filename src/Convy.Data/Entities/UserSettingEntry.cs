using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Convy.Data.Entities
{
	[PrimaryKey(nameof(Key))]
	public class UserSettingEntry
	{
		[MaxLength(256)]
		public required string Key { get; set; }

		[MaxLength(2048)]
		public required string Value { get; set; }
	}
}
