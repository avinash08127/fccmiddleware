package com.fccmiddleware.edge.ui.theme

import androidx.compose.foundation.Image
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.fccmiddleware.edge.R

@Composable
fun SectionHeader(title: String) {
    Text(
        text = title,
        fontSize = 15.sp,
        fontWeight = FontWeight.Bold,
        color = PumaGreen,
        modifier = Modifier.padding(top = 12.dp, bottom = 4.dp),
    )
}

@Composable
fun DataRow(label: String, value: String, valueColor: Color = TextPrimary) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 2.dp),
    ) {
        Text(
            text = label,
            fontSize = 14.sp,
            color = TextLabel,
            modifier = Modifier.weight(1f),
        )
        Text(
            text = value,
            fontSize = 14.sp,
            fontWeight = FontWeight.Bold,
            color = valueColor,
            textAlign = TextAlign.End,
            modifier = Modifier.weight(1f),
        )
    }
}

@Composable
fun MonoDataRow(label: String, value: String, valueColor: Color = TextGray) {
    Text(
        text = label,
        fontSize = 12.sp,
        color = TextLabel,
    )
    Text(
        text = value,
        fontSize = 12.sp,
        fontFamily = FontFamily.Monospace,
        color = valueColor,
    )
}

@Composable
fun PumaButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    color: Color = PumaGreen,
) {
    Button(
        onClick = onClick,
        enabled = enabled,
        colors = ButtonDefaults.buttonColors(containerColor = color),
        shape = RoundedCornerShape(8.dp),
        modifier = modifier,
    ) {
        Text(text.uppercase(), color = Color.White)
    }
}

@Composable
fun PumaOutlinedButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    borderColor: Color = PumaGreen,
) {
    OutlinedButton(
        onClick = onClick,
        shape = RoundedCornerShape(8.dp),
        colors = ButtonDefaults.outlinedButtonColors(contentColor = borderColor),
        border = BorderStroke(2.dp, borderColor),
        modifier = modifier,
    ) {
        Text(text.uppercase(), color = borderColor)
    }
}

@Composable
fun PumaLogo(modifier: Modifier = Modifier) {
    Image(
        painter = painterResource(id = R.drawable.puma_energy_logo),
        contentDescription = "Puma Energy Logo",
        modifier = modifier,
    )
}

@Composable
fun RedAccentDivider(modifier: Modifier = Modifier) {
    Spacer(
        modifier = modifier
            .width(60.dp)
            .height(3.dp)
            .then(
                Modifier.padding(bottom = 16.dp)
            ),
    )
}
