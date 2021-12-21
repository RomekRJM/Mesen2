﻿using Avalonia.Controls;
using Avalonia.Rendering;
using Mesen.Localization;
using Mesen.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Mesen.Utilities
{
	public class MesenMsgBox
	{
		public static Task<DialogResult> Show(IRenderRoot? parent, string text, MessageBoxButtons buttons, MessageBoxIcon icon, params string[] args)
		{
			if(!(parent is Window wnd)) {
				throw new Exception("Invalid parent window");
			}

			string resourceText = ResourceHelper.GetMessage(text, args);

			if(resourceText.StartsWith("[[")) {
				if(args != null && args.Length > 0) {
					return MessageBox.Show(wnd, string.Format("Critical error (" + text + ") {0}", args), "Mesen", buttons, icon);
				} else {
					return MessageBox.Show(wnd, string.Format("Critical error (" + text + ")"), "Mesen", buttons, icon);
				}
			} else {
				return MessageBox.Show(wnd, ResourceHelper.GetMessage(text, args), "Mesen", buttons, icon);
			}
		}
	}
}
