using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(BitMaskPropertyAttribute))]
public class BitMaskPropertyDrawer : PropertyDrawer
{
	// Draw the property inside the given rect
	public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
	{
		// First get the attribute since it contains the range for the slider
		BitMaskPropertyAttribute range = (BitMaskPropertyAttribute)attribute;
		
		// shrink position to make space for the label
		float labelWidth = position.width * 0.15f;
		float labelEndX = position.xMin + labelWidth;
		EditorGUI.LabelField(new Rect(
			position.x,
			position.y,
			labelWidth,
			position.height
		), label);
		position.xMin = labelEndX;
		
		int value = property.intValue;
		const int bitRange = 32;
		float widthPerField = position.width / (float) bitRange;
		for (int i = 0; i < bitRange; i++) {
			float i01 = i / (float) bitRange;
			Rect rect = new Rect(
				Mathf.Lerp(position.xMax, position.xMin, 1f - i01),
				position.y,
				widthPerField,
				position.height
			);
			
			EditorGUI.Toggle(rect, ((1 << i) & value) == value);
		}

	}
}
